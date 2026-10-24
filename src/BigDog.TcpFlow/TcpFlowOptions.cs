using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BigDog.TcpFlow.Serializer;

namespace BigDog.TcpFlow;

/// <summary>Settings shared by <see cref="TcpFlowServer"/> and <see cref="TcpFlowClient"/>.</summary>
public abstract class TcpFlowOptions
{
    /// <summary>The largest payload accepted or sent, in bytes. Larger incoming frames close the connection. Default 4 MiB.</summary>
    public int MaxMessageSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>The serializer. Both ends must use the same one. Default <see cref="MemoryPackFlowSerializer"/>.</summary>
    public IFlowSerializer Serializer { get; init; } = MemoryPackFlowSerializer.Default;

    /// <summary>The known flow types. Default <see cref="FlowTypeRegistry.Default"/>, which the generator fills in.</summary>
    public FlowTypeRegistry Registry { get; init; } = FlowTypeRegistry.Default;

    /// <summary>Where diagnostics are logged. Default: nowhere.</summary>
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

    /// <summary>Disables Nagle's algorithm so small messages go out immediately. Default <c>true</c>.</summary>
    public bool NoDelay { get; init; } = true;

    /// <summary>How long a TLS handshake may take before the connection is dropped. Default 10 seconds.</summary>
    public TimeSpan TlsHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Allowed TLS versions. Default <see cref="SslProtocols.None"/>, which lets the OS pick secure versions.</summary>
    public SslProtocols EnabledSslProtocols { get; init; } = SslProtocols.None;

    /// <summary>Whether to check the peer certificate's revocation status. Default <c>true</c>.</summary>
    public bool CheckCertificateRevocation { get; init; } = true;

    internal virtual void Validate()
    {
        if (MaxMessageSize <= 0 || MaxMessageSize > Array.MaxLength - 8)
            throw new ArgumentOutOfRangeException(nameof(MaxMessageSize), MaxMessageSize, "MaxMessageSize must be positive and fit in an array.");
        if (TlsHandshakeTimeout <= TimeSpan.Zero && TlsHandshakeTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(TlsHandshakeTimeout), TlsHandshakeTimeout, "TlsHandshakeTimeout must be positive or infinite.");
        ArgumentNullException.ThrowIfNull(Serializer);
        ArgumentNullException.ThrowIfNull(Registry);
        ArgumentNullException.ThrowIfNull(LoggerFactory);
    }
}

/// <summary>Settings for <see cref="TcpFlowServer"/>.</summary>
public sealed class TcpFlowServerOptions : TcpFlowOptions
{
    /// <summary>The port to listen on. Use 0 to pick a free port and read it from <see cref="TcpFlowServer.LocalEndPoint"/>.</summary>
    public required int Port { get; init; }

    /// <summary>The address to bind. Default <c>null</c>: all interfaces, IPv4 and IPv6.</summary>
    public IPAddress? Address { get; init; }

    /// <summary>When set, every connection must complete a TLS handshake using this certificate (which needs its private key).</summary>
    public X509Certificate2? ServerCertificate { get; init; }

    /// <summary>Require clients to present a certificate (mutual TLS). Requires <see cref="ServerCertificate"/>.</summary>
    public bool RequireClientCertificate { get; init; }

    /// <summary>Custom validation for client certificates. Default: standard chain validation.</summary>
    public RemoteCertificateValidationCallback? ClientCertificateValidation { get; init; }

    internal override void Validate()
    {
        base.Validate();
        if (Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port is out of range.");
        if (ServerCertificate is null && (RequireClientCertificate || ClientCertificateValidation is not null))
            throw new ArgumentException("Client certificate options require a ServerCertificate.", nameof(ServerCertificate));
    }
}

/// <summary>Settings for <see cref="TcpFlowClient"/>.</summary>
public sealed class TcpFlowClientOptions : TcpFlowOptions
{
    /// <summary>The server host name or IP address.</summary>
    public required string Host { get; init; }

    /// <summary>The server port.</summary>
    public required int Port { get; init; }

    /// <summary>Connect over TLS. The server certificate is validated normally unless one of the options below changes that.</summary>
    public bool UseTls { get; init; }

    /// <summary>The name expected in the server certificate. Default <see cref="Host"/>.</summary>
    public string? TargetHost { get; init; }

    /// <summary>
    /// Trust exactly this server certificate (compared by SHA-256 hash), ignoring chain and name errors.
    /// Useful for self-signed certificates.
    /// </summary>
    public X509Certificate2? PinnedServerCertificate { get; init; }

    /// <summary>A certificate to present to the server for mutual TLS.</summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>Custom server certificate validation. Takes precedence over <see cref="PinnedServerCertificate"/>.</summary>
    public RemoteCertificateValidationCallback? ServerCertificateValidation { get; init; }

    internal override void Validate()
    {
        base.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        if (Port is < 1 or > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port is out of range.");
        if (!UseTls && (TargetHost is not null || PinnedServerCertificate is not null
                        || ClientCertificate is not null || ServerCertificateValidation is not null))
            throw new ArgumentException("TLS options are set but UseTls is false.", nameof(UseTls));
    }
}
