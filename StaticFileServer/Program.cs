using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using StaticFileServer;
using System.Security.Cryptography.X509Certificates;

// 1) Serilog の初期化
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()                         // コンソール
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "Logs", "app-.log"),             // ファイル（Rolling 日別）
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)             // 7日分残す
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// 設定読み込み
var useHttps = builder.Configuration.GetValue("Hosting:UseHttps", false);
var httpPort = builder.Configuration.GetValue("Hosting:HttpPort", 8080);
var httpsPort = builder.Configuration.GetValue("Hosting:HttpsPort", 8443);
var crtPath = builder.Configuration.GetValue<string>("Hosting:Certificate:CrtPath");
var keyPath = builder.Configuration.GetValue<string>("Hosting:Certificate:KeyPath");

// Windows サービスとして動く場合の設定
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "MinimalStaticFileServer"; // 好きなサービス名
    });

    // サービス実行時は作業ディレクトリが system32 になることが多いので、
    // 何か相対パスを使うなら AppContext.BaseDirectory を使うのが吉
}

// appsettings.json の Kestrel セクションが自動で反映される
// builder.WebHost.ConfigureKestrel(...) を空で呼ぶだけでも可
//builder.WebHost.ConfigureKestrel(_ => { });
builder.WebHost.ConfigureKestrel(options =>
{
    if (useHttps && !string.IsNullOrEmpty(crtPath) && !string.IsNullOrEmpty(keyPath)
        && File.Exists(crtPath) && File.Exists(keyPath))
    {
        // .crt / .key を読み込んで X509Certificate2 を生成
        var cert = X509Certificate2.CreateFromPemFile(crtPath, keyPath);
        cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));

        options.ListenAnyIP(httpsPort, listenOpts =>
        {
            listenOpts.UseHttps(cert);
        });
    }
    else
    {
        // fallback to HTTP
        options.ListenAnyIP(httpPort);
    }
});

// 2) 既存ロガーを Serilog に差し替え
builder.Host.UseSerilog();

// 圧縮を有効化
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true; // HTTPSでも有効
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.AddHttpLogging(logging =>
{
    // ログに何を出すか制御できる（必要に応じて絞る）
    logging.LoggingFields = HttpLoggingFields.RequestPath |
                            HttpLoggingFields.RequestMethod |
                            HttpLoggingFields.ResponseStatusCode;
});

var app = builder.Build();

// クライアント IP ログ Middleware
app.Use(async (context, next) =>
{
    var ip = context.Connection.RemoteIpAddress?.ToString();
    var method = context.Request.Method;
    var path = context.Request.Path;

    // Serilog に出力
    Log.Information("Client {IP} {Method} {Path}", ip, method, path);

    await next.Invoke();
});

// 1) アクセスログを出す（最初に）
app.UseHttpLogging();

// 2) 圧縮を有効化
app.UseResponseCompression();

// ❶ 例外ハンドラ（できれば最初の中間層のほうに置く）
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async ctx =>
    {
        var exFeature = ctx.Features.Get<IExceptionHandlerPathFeature>();
        var ex = exFeature?.Error;

        // ログ（Serilog を既に使っている前提）
        Serilog.Log.Error(ex, "Unhandled exception at {Path}", exFeature?.Path);

        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;

        // キャッシュさせない
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["Pragma"] = "no-cache";

        // Accept ヘッダで応答フォーマットを切替
        var accept = ctx.Request.GetTypedHeaders().Accept;
        var wantsHtml = accept?.Any(a => a.MediaType.HasValue &&
                                         a.MediaType.Value.Contains("text/html", StringComparison.OrdinalIgnoreCase)) == true;

        // ❷ HTML: build/500.html があればそれを返す
        var distPath = Path.Combine(AppContext.BaseDirectory, "build");
        var errorHtml = Path.Combine(distPath, "500.html");

        if (wantsHtml && File.Exists(errorHtml))
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(errorHtml);
            return;
        }

        // ❸ JSON（ProblemDetails）
        ctx.Response.ContentType = "application/problem+json; charset=utf-8";
        var problem = new ProblemDetails
        {
            Title = "Internal Server Error",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "An unexpected error occurred.",
            Instance = exFeature?.Path
        };
        await ctx.Response.WriteAsJsonAsync(problem);
    });
});

// 公開するフォルダ（例: build/ または dist/）
var distPath = Path.Combine(AppContext.BaseDirectory, "build");
var provider = new PhysicalFileProvider(distPath);

// MIME 拡張 (例: .wasm)
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".wasm"] = "application/wasm";

// /assets 以下は長期キャッシュ (1年, immutable)
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/assets"), branch =>
{
    branch.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = provider,
        ContentTypeProvider = contentTypeProvider,
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers["Cache-Control"] =
                "public, max-age=31536000, immutable";
        }
    });
});

// それ以外の静的ファイル
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = provider,
    ContentTypeProvider = contentTypeProvider,
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-store";
        }
        else
        {
            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=600";
        }
        ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
});

// 404: 存在しないURLは build/404.html を返す
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

// ヘルスチェック (疎通確認用)
app.MapGet("/healthz", () =>
{
    return Results.Ok(new
    {
        status = "ok",
        timestamp = DateTimeOffset.UtcNow
    });
});

// レディネスチェック (依存リソース確認用)
app.MapGet("/readyz", () =>
{
    var checks = new Dictionary<string, object>();

    // 1) 静的ファイルフォルダの存在確認（外側の distPath をそのまま使う）
    checks["staticFiles"] = Directory.Exists(distPath);

    // 2) Logs フォルダ書き込み可能か確認
    try
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "Logs", "write-test.txt");
        File.WriteAllText(logPath, DateTime.UtcNow.ToString("O"));
        File.Delete(logPath);
        checks["logWritable"] = true;
    }
    catch
    {
        checks["logWritable"] = false;
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
});

app.Run();
