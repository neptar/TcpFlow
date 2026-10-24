using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace BigDog.TcpFlow.Example.Demos;

/// <summary>
/// TLS with a self-signed certificate. The client pins the certificate so it trusts exactly that one; a client
/// without the pin is rejected, because server certificates are validated by default.
/// </summary>
public static class TlsDemo
{
    public static async Task RunAsync(ILoggerFactory loggerFactory)
    {
        // In production load a real certificate, e.g. X509CertificateLoader.LoadPkcs12FromFile("server.pfx", password).
        using var certificate = CreateSelfSignedCertificate("localhost");
        Console.WriteLine($"Server certificate: {certificate.Subject}, SHA-256 {certificate.GetCertHashString(HashAlgorithmName.SHA256)[..16]}…");

        await using var server = new TcpFlowServer(new TcpFlowServerOptions
        {
            Port = 0,
            Address = IPAddress.Loopback,
            ServerCertificate = certificate,
            LoggerFactory = loggerFactory,
        });
        server.Map<Command>(CommandsDemo.HandleCommandAsync);
        server.Start();
        var port = server.LocalEndPoint!.Port;

        // 1. Pinned: trusted even though no CA signed it.
        var responses = Channel.CreateUnbounded<CommandResponse>();
        await using (var pinned = new TcpFlowClient(new TcpFlowClientOptions
                     {
                         Host = "localhost",
                         Port = port,
                         UseTls = true,
                         PinnedServerCertificate = certificate,
                         LoggerFactory = loggerFactory,
                     }))
        {
            pinned.Map<CommandResponse>((r, _) => responses.Writer.TryWrite(r));
            await pinned.ConnectAsync();
            await pinned.SendAsync(new Command { Cmd = "echo", Arg = "this travelled encrypted" });
            var response = await responses.Reader.ReadAsync();
            Console.WriteLine($"Pinned client:    connected (encrypted: {pinned.Connection!.IsEncrypted}), got \"{response.Message}\"");
        }

        // 2. Not pinned: standard validation rejects a self-signed certificate.
        await using var unpinned = new TcpFlowClient(new TcpFlowClientOptions
        {
            Host = "localhost",
            Port = port,
            UseTls = true,
            LoggerFactory = loggerFactory,
        });
        try
        {
            await unpinned.ConnectAsync();
            Console.WriteLine("Unpinned client:  connected (unexpected!)");
        }
        catch (AuthenticationException ex)
        {
            Console.WriteLine($"Unpinned client:  rejected as expected ({ex.Message})");
        }
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string hostName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Round-trip through PFX so the private key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}
