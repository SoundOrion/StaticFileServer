using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Serilog;

// 1) Serilog �̏�����
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()                         // �R���\�[��
    .WriteTo.File("Logs/app-.log",             // �t�@�C���iRolling ���ʁj
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)             // 7�����c��
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// appsettings.json �� Kestrel �Z�N�V�����������Ŕ��f�����
// builder.WebHost.ConfigureKestrel(...) ����ŌĂԂ����ł���
builder.WebHost.ConfigureKestrel(_ => { });

// 2) �������K�[�� Serilog �ɍ����ւ�
builder.Host.UseSerilog();

// ���k��L����
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true; // HTTPS�ł��L��
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.AddHttpLogging(logging =>
{
    // ���O�ɉ����o��������ł���i�K�v�ɉ����či��j
    logging.LoggingFields = HttpLoggingFields.RequestPath |
                            HttpLoggingFields.RequestMethod |
                            HttpLoggingFields.ResponseStatusCode;
});

var app = builder.Build();

// 1) �A�N�Z�X���O���o���i�ŏ��Ɂj
app.UseHttpLogging();

// 2) ���k��L����
app.UseResponseCompression();

// ���J����t�H���_�i��: build/ �܂��� dist/�j
var distPath = Path.Combine(AppContext.BaseDirectory, "build");
var provider = new PhysicalFileProvider(distPath);

// MIME �g�� (��: .wasm)
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".wasm"] = "application/wasm";

// /assets �ȉ��͒����L���b�V�� (1�N, immutable)
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

// ����ȊO�̐ÓI�t�@�C��
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

// 404: ���݂��Ȃ�URL�� build/404.html ��Ԃ�
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
