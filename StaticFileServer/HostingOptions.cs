using System.ComponentModel.DataAnnotations;

namespace StaticFileServer;

public sealed class HostingOptions
{
    public bool UseHttps { get; set; } = false;

    [Range(1, 65535)]
    public int HttpPort { get; set; } = 8080;

    [Range(1, 65535)]
    public int HttpsPort { get; set; } = 8443;

    public CertificateOptions Certificate { get; set; } = new();
}

public sealed class CertificateOptions
{
    // UseHttps=false のときは未設定でもOKにしたいので Required は付けない
    public string? CrtPath { get; set; } = "certs/server.crt";
    public string? KeyPath { get; set; } = "certs/server.key";
}
