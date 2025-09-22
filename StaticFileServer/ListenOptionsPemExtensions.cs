using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace StaticFileServer;

public static class CertificateExtensions
{
    /// <summary>
    /// PEM (crt/key) から証明書を読み込み、エフェメラルキーで再インポートします。
    /// </summary>
    public static X509Certificate2 LoadEphemeralFromPem(string certPemPath, string keyPemPath)
    {
        var fromPem = X509Certificate2.CreateFromPemFile(certPemPath, keyPemPath);
        byte[] pfx = fromPem.Export(X509ContentType.Pfx);
        try
        {
            return new X509Certificate2(
            pfx,
            (string?)null,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.MachineKeySet
            );
        }
        finally
        {
            Array.Clear(pfx, 0, pfx.Length);
        }
    }
}