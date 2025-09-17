using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;

#pragma warning disable ASP0000

// ---------------------------
// 1) Serilog 初期化
// ---------------------------
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "Logs", "app-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true
    )
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilog を Host にバインド（appsettings も読めるように）
    builder.Host.UseSerilog((ctx, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(AppContext.BaseDirectory, "Logs", "app-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true
        )
    );

    // ---------------------------
    // 2) 設定読み込み
    // ---------------------------
    var useHttps = builder.Configuration.GetValue("Hosting:UseHttps", false);
    var httpPort = builder.Configuration.GetValue("Hosting:HttpPort", 8080);
    var httpsPort = builder.Configuration.GetValue("Hosting:HttpsPort", 8443);
    var crtPath = builder.Configuration.GetValue<string>("Hosting:Certificate:CrtPath");
    var keyPath = builder.Configuration.GetValue<string>("Hosting:Certificate:KeyPath");

    // Windows サービス対応
    if (WindowsServiceHelpers.IsWindowsService())
    {
        builder.Host.UseWindowsService(options =>
        {
            options.ServiceName = "MinimalStaticFileServer";
        });
        // サービス時の作業ディレクトリ対策
        builder.Host.UseContentRoot(AppContext.BaseDirectory);
    }

    // ---------------------------
    // 3) Kestrel 設定
    // ---------------------------
    builder.WebHost.ConfigureKestrel(options =>
    {
        // Server ヘッダ除去
        options.AddServerHeader = false;

        // 接続/リクエスト制限（必要に応じ調整）
        options.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
        options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);

        if (useHttps && !string.IsNullOrWhiteSpace(crtPath) && !string.IsNullOrWhiteSpace(keyPath)
            && File.Exists(crtPath) && File.Exists(keyPath))
        {
            var cert = X509Certificate2.CreateFromPemFile(crtPath, keyPath);
            cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));

            options.ListenAnyIP(httpsPort, listenOpts =>
            {
                listenOpts.Protocols = HttpProtocols.Http1AndHttp2;
                listenOpts.UseHttps(cert, https =>
                {
                    // 必要に応じて TLS バージョン/暗号スイートの制御を追加
                });
            });
        }
        else
        {
            // fallback to HTTP
            options.ListenAnyIP(httpPort, o =>
            {
                o.Protocols = HttpProtocols.Http1AndHttp2;
            });
        }
    });

    // ---------------------------
    // 4) ForwardedHeaders（逆プロキシ対応）
    // ---------------------------
    builder.Services.Configure<ForwardedHeadersOptions>(opts =>
    {
        opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // 可能なら信頼できるプロキシ/ネットワークを明示
        // opts.KnownProxies.Add(IPAddress.Parse("10.0.0.1"));
        // opts.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
    });

    // ---------------------------
    // 5) 圧縮（MIME + レベル）
    // ---------------------------
    builder.Services.AddResponseCompression(opts =>
    {
        opts.EnableForHttps = true;
        opts.Providers.Add<BrotliCompressionProvider>();
        opts.Providers.Add<GzipCompressionProvider>();
        opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
        {
            "image/svg+xml",
            "application/wasm",
            "application/json",
            "application/xml",
            "text/plain",
            "text/css",
            "application/javascript"
        });
    });
    builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
    builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);

    // ---------------------------
    // 6) HTTP Logging（必要なら最小限）
    // ---------------------------
    builder.Services.AddHttpLogging(logging =>
    {
        logging.LoggingFields = HttpLoggingFields.RequestPath |
                                HttpLoggingFields.RequestMethod |
                                HttpLoggingFields.ResponseStatusCode;
    });

    // ---------------------------
    // 7) ProblemDetails（標準 500 応答基盤）
    // ---------------------------
    builder.Services.AddProblemDetails();

    // ---------------------------
    // 8) レート制限（簡易 DoS 緩和）
    // ---------------------------
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));
    });

    // ---------------------------
    // 9) Windows 認証（Negotiate）＋ 認可
    // ---------------------------
    builder.Services
        .AddAuthentication(NegotiateDefaults.AuthenticationScheme)
        .AddNegotiate();

    builder.Services.AddAuthorization(options =>
    {
        // 既定は「認証必須」
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        // 匿名を許すポリシー（ヘルスチェック等で使用）
        options.AddPolicy("AllowAnonymous", p => p.RequireAssertion(_ => true));
    });

    var app = builder.Build();

    // ---------------------------
    // 10) 例外ハンドラ（最上流）
    // ---------------------------
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async ctx =>
        {
            var exFeature = ctx.Features.Get<IExceptionHandlerPathFeature>();
            var ex = exFeature?.Error;

            Log.Error(ex, "Unhandled exception at {Path}", exFeature?.Path);

            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Pragma"] = "no-cache";

            // Accept で切替
            var accept = ctx.Request.GetTypedHeaders().Accept;
            var wantsHtml = accept?.Any(a => a.MediaType.HasValue &&
                         a.MediaType.Value.Contains("text/html", StringComparison.OrdinalIgnoreCase)) == true;

            var distPathLocal = Path.Combine(AppContext.BaseDirectory, "build");
            var errorHtml = Path.Combine(distPathLocal, "500.html");

            if (wantsHtml && File.Exists(errorHtml))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(errorHtml);
                return;
            }

            var traceId = System.Diagnostics.Activity.Current?.Id ?? ctx.TraceIdentifier;
            ctx.Response.ContentType = "application/problem+json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Internal Server Error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred.",
                Instance = exFeature?.Path,
                Extensions = { ["traceId"] = traceId }
            });
        });
    });

    // 逆プロキシヘッダ適用
    app.UseForwardedHeaders();

    // HSTS/HTTPS リダイレクト（本番かつ HTTPS 有効時）
    if (!app.Environment.IsDevelopment() && useHttps)
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    // ---------------------------
    // 11) 認証/認可（ユーザー名をログに入れるため Serilog より前に）
    // ---------------------------
    app.UseAuthentication();
    app.UseAuthorization();

    // ---------------------------
    // 12) Serilog リクエストログ（処理時間など）
    // ---------------------------
    app.UseSerilogRequestLogging(opts =>
    {
        // 低頻度にしたいパスを減衰（ヘルスやアセット）
        opts.GetLevel = (ctx, elapsed, ex) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.StartsWith("/healthz") || path.StartsWith("/readyz") || path.StartsWith("/assets"))
                return Serilog.Events.LogEventLevel.Verbose;

            return (ex is not null || ctx.Response.StatusCode >= 500)
                ? Serilog.Events.LogEventLevel.Error
                : Serilog.Events.LogEventLevel.Information;
        };

        opts.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms from {RemoteIpAddress} (User={UserName})";

        // ユーザー名/クライアント IP をログに埋め込む
        opts.EnrichDiagnosticContext = (diagCtx, httpCtx) =>
        {
            var ip = httpCtx.Connection.RemoteIpAddress;
            if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
            diagCtx.Set("RemoteIpAddress", ip?.ToString());

            var userName = (httpCtx.User?.Identity?.IsAuthenticated == true)
                ? httpCtx.User!.Identity!.Name // 例: "CORP\\yamada"
                : "anonymous";
            diagCtx.Set("UserName", userName);
        };
    });

    // HTTP ログ（必要なら）
    app.UseHttpLogging();

    // 圧縮
    app.UseResponseCompression();

    // レート制限
    app.UseRateLimiter();

    // ---------------------------
    // 13) 静的ファイル設定
    // ---------------------------
    var distPath = Path.Combine(AppContext.BaseDirectory, "build");
    var provider = new PhysicalFileProvider(distPath);

    // MIME 拡張
    var contentTypeProvider = new FileExtensionContentTypeProvider();
    contentTypeProvider.Mappings[".wasm"] = "application/wasm";
    contentTypeProvider.Mappings[".txt"] = "text/plain; charset=utf-8";
    contentTypeProvider.Mappings[".log"] = "text/plain; charset=utf-8";
    contentTypeProvider.Mappings[".csv"] = "text/csv; charset=utf-8";
    contentTypeProvider.Mappings[".json"] = "application/json; charset=utf-8";

    // セキュリティヘッダ付与
    static void AddSecurityHeaders(HttpContext ctx)
    {
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        // 必要に応じて CSP を設定（SPA/外部CDNに合わせて調整）
        // ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self';";
    }

    // /assets は長期キャッシュ
    app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/assets"), branch =>
    {
        branch.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            ContentTypeProvider = contentTypeProvider,
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                AddSecurityHeaders(ctx.Context);
            }
        });
    });

    // その他の静的ファイル
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = provider,
        ContentTypeProvider = contentTypeProvider,
        OnPrepareResponse = ctx =>
        {
            if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                ctx.Context.Response.Headers["Cache-Control"] = "no-store";
            else
                ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=600";

            AddSecurityHeaders(ctx.Context);
        }
    });

    // ---------------------------
    // 14) Fallback（404.html を返す）
    // ---------------------------
    app.MapFallback(async ctx =>
    {
        var notfound = Path.Combine(distPath, "404.html");
        if (File.Exists(notfound))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(notfound);
        }
        else
        {
            ctx.Response.StatusCode = 404;
        }
    });

    // ---------------------------
    // 15) ヘルス/レディネス（匿名許可）
    // ---------------------------
    app.MapGet("/healthz", [AllowAnonymous] () =>
    {
        return Results.Ok(new
        {
            status = "ok",
            timestamp = DateTimeOffset.UtcNow
        });
    }).RequireAuthorization("AllowAnonymous");

    app.MapGet("/readyz", [AllowAnonymous] () =>
    {
        var checks = new Dictionary<string, object>();

        // 静的ファイルフォルダの存在
        var distPathLocal = Path.Combine(AppContext.BaseDirectory, "build");
        checks["staticFiles"] = Directory.Exists(distPathLocal);

        // Logs フォルダ書込可
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "Logs", "write-test.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, DateTime.UtcNow.ToString("O"));
            File.Delete(logPath);
            checks["logWritable"] = true;
        }
        catch
        {
            checks["logWritable"] = false;
        }

        // 証明書有効期限（useHttps 時のみ）
        if (useHttps && !string.IsNullOrWhiteSpace(crtPath) && !string.IsNullOrWhiteSpace(keyPath)
            && File.Exists(crtPath) && File.Exists(keyPath))
        {
            try
            {
                using var c = new X509Certificate2(
                    X509Certificate2.CreateFromPemFile(crtPath, keyPath).Export(X509ContentType.Pfx)
                );
                checks["certNotExpired"] = c.NotAfter > DateTime.UtcNow;
                checks["certDaysLeft"] = Math.Max(0, (int)(c.NotAfter - DateTime.UtcNow).TotalDays);
            }
            catch
            {
                checks["certNotExpired"] = false;
            }
        }

        var allOk = checks.Values.All(v => v is bool b && b);

        if (allOk)
        {
            return Results.Ok(new
            {
                status = "ready",
                timestamp = DateTimeOffset.UtcNow,
                checks
            });
        }
        else
        {
            return Results.Json(new
            {
                status = "not-ready",
                timestamp = DateTimeOffset.UtcNow,
                checks
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).RequireAuthorization("AllowAnonymous");

    // 任意：バージョン表示（デプロイ確認に）
    app.MapGet("/version", () =>
    {
        var asm = Assembly.GetEntryAssembly();
        var infoVer = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm?.GetName().Version?.ToString()
                      ?? "unknown";
        return Results.Ok(new { version = infoVer, timestamp = DateTimeOffset.UtcNow });
    });

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
