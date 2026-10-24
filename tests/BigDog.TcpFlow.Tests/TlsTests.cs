using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using static BigDog.TcpFlow.Tests.TestHelpers;

namespace BigDog.TcpFlow.Tests;

public class TlsTests
{
    private static X509Certificate2 CreateSelfSignedCertificate(string subject = "localhost")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(subject);
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Round-trip through PFX so the private key is usable by SslStream on every platform.
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
#else
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
#endif
    }

    private static TcpFlowServer StartTlsServer(X509Certificate2 certificate, Action<TcpFlowServer>? configure = null)
    {
        var server = new TcpFlowServer(new TcpFlowServerOptions
        {
            Port = 0,
            Address = IPAddress.Loopback,
            ServerCertificate = certificate,
        });
        configure?.Invoke(server);
        server.Start();
        return server;
    }

    [Fact]
    public async Task Pinned_self_signed_certificate_round_trips()
    {
        using var certificate = CreateSelfSignedCertificate();
        var received = new TaskCompletionSource<Ping>();
        await using var server = StartTlsServer(certificate, s => s.Map<Ping>((p, _) => received.TrySetResult(p)));

        await using var client = new TcpFlowClient(new TcpFlowClientOptions
        {
            Host = "127.0.0.1",
            Port = server.LocalEndPoint!.Port,
            UseTls = true,
            TargetHost = "localhost",
            PinnedServerCertificate = certificate,
        });
        await client.ConnectAsync();
        await client.SendAsync(new Ping { Text = "secret" });

        Assert.Equal("secret", (await received.WithTimeout()).Text);
        Assert.True(client.Connection!.IsEncrypted);
    }

    [Fact]
    public async Task Untrusted_certificate_is_rejected_by_default()
    {
        using var certificate = CreateSelfSignedCertificate();
        await using var server = StartTlsServer(certificate);

        await using var client = new TcpFlowClient(new TcpFlowClientOptions
        {
            Host = "127.0.0.1",
            Port = server.LocalEndPoint!.Port,
            UseTls = true,
            TargetHost = "localhost",
        });

        await Assert.ThrowsAsync<AuthenticationException>(() => client.ConnectAsync());
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Pinning_a_different_certificate_is_rejected()
    {
        using var serverCertificate = CreateSelfSignedCertificate();
        using var otherCertificate = CreateSelfSignedCertificate();
        await using var server = StartTlsServer(serverCertificate);

        await using var client = new TcpFlowClient(new TcpFlowClientOptions
        {
            Host = "127.0.0.1",
            Port = server.LocalEndPoint!.Port,
            UseTls = true,
            TargetHost = "localhost",
            PinnedServerCertificate = otherCertificate,
        });

        await Assert.ThrowsAsync<AuthenticationException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task Plain_tcp_client_cannot_talk_to_a_tls_server()
    {
        using var certificate = CreateSelfSignedCertificate();
        var received = new TaskCompletionSource<Ping>();
        await using var server = StartTlsServer(certificate, s => s.Map<Ping>((p, _) => received.TrySetResult(p)));
        await using var client = await ConnectAsync(server);

        try
        {
            await client.SendAsync(new Ping());
        }
        catch (IOException)
        {
        }

        await Task.Delay(200);
        Assert.False(received.Task.IsCompleted);
    }

    [Fact]
    public void Tls_options_without_UseTls_are_rejected()
    {
        using var certificate = CreateSelfSignedCertificate();
        Assert.Throws<ArgumentException>(() => new TcpFlowClient(new TcpFlowClientOptions
        {
            Host = "localhost",
            Port = 1,
            PinnedServerCertificate = certificate,
        }));
    }
}
