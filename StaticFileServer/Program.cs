using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Serilog;

// 1) Serilog の初期化
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()                         // コンソール
    .WriteTo.File("Logs/app-.log",             // ファイル（Rolling 日別）
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)             // 7日分残す
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// appsettings.json の Kestrel セクションが自動で反映される
// builder.WebHost.ConfigureKestrel(...) を空で呼ぶだけでも可
builder.WebHost.ConfigureKestrel(_ => { });

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

// 1) アクセスログを出す（最初に）
app.UseHttpLogging();

// 2) 圧縮を有効化
app.UseResponseCompression();

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

app.Run();
