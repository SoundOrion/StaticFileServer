using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Cryptography.X509Certificates;

namespace StaticFileServer;

public static class ListenOptionsPemExtensions
{
    /// <summary>
    /// Kestrel に .crt/.key (PEM) ファイルから証明書を読み込ませる。
    /// </summary>
    /// <param name="listenOptions">対象の ListenOptions</param>
    /// <param name="certPemPath">証明書ファイル (.crt or .pem)</param>
    /// <param name="keyPemPath">秘密鍵ファイル (.key)</param>
    /// <returns></returns>
    public static ListenOptions UseHttpsFromPem(this ListenOptions listenOptions, string certPemPath, string keyPemPath)
    {
        // .NET 6+ なら CreateFromPemFile が使える
        var cert = X509Certificate2.CreateFromPemFile(certPemPath, keyPemPath);

        // 秘密鍵付きに変換 (.pfx形式に一時エクスポート → 再インポート)
        cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));

        return listenOptions.UseHttps(cert);
    }
}