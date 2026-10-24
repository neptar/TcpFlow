using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BigDog.TcpFlow.Internal;

namespace BigDog.TcpFlow;

/// <summary>Connects to a <see cref="TcpFlowServer"/>, sends messages and dispatches messages the server pushes back.</summary>
public sealed class TcpFlowClient : TcpFlowEndpoint
{
    private readonly TcpFlowClientOptions _options;
    private readonly byte[]? _pinnedCertificateHash;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private TcpFlowConnection? _connection;
    private Task? _readLoop;
    private long _lastConnectionId;
    private int _disposed;

    /// <summary>Creates a client for <paramref name="host"/>:<paramref name="port"/> with default options (no TLS).</summary>
    public TcpFlowClient(string host, int port) : this(new TcpFlowClientOptions { Host = host, Port = port })
    {
    }

    /// <summary>Creates a client. Call <see cref="ConnectAsync"/> to connect.</summary>
    public TcpFlowClient(TcpFlowClientOptions options) : base(options, "BigDog.TcpFlow.TcpFlowClient")
    {
        _options = options;
        _pinnedCertificateHash = options.PinnedServerCertificate?.GetCertHash(HashAlgorithmName.SHA256);
    }

    /// <summary>Raised after the connection closes, whether the server, the network or this client closed it.</summary>
    public event EventHandler<TcpFlowConnection>? Disconnected;

    /// <summary>The current (or most recent) connection.</summary>
    public TcpFlowConnection? Connection => _connection;

    /// <summary>Whether the client is connected.</summary>
    public bool IsConnected => _connection?.IsConnected == true;

    /// <summary>
    /// Connects (and completes the TLS handshake, if enabled). Returns once the client can send.
    /// Can be called again after a disconnect to reconnect.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already connected.</exception>
    /// <exception cref="SocketException">The server couldn't be reached.</exception>
    /// <exception cref="System.Security.Authentication.AuthenticationException">The TLS handshake failed, e.g. the server certificate wasn't trusted.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                throw new InvalidOperationException("The client is already connected.");

            var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);
                tcp.NoDelay = Options.NoDelay;
                var stream = await AuthenticateAsync(tcp, cancellationToken).ConfigureAwait(false);

                var connection = new TcpFlowConnection(Interlocked.Increment(ref _lastConnectionId), tcp, stream, this, _shutdown.Token);
                _connection = connection;
                Log.Connected(Logger, _options.Host, _options.Port);
                _readLoop = RunAsync(connection);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task RunAsync(TcpFlowConnection connection)
    {
        await connection.RunAsync().ConfigureAwait(false);
        Log.ConnectionClosed(Logger, connection.Id, connection.RemoteEndPoint);
        RaiseEvent(Disconnected, connection);
    }

    private async Task<Stream> AuthenticateAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        var network = tcp.GetStream();
        if (!_options.UseTls)
            return network;

        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Options.TlsHandshakeTimeout);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _options.TargetHost ?? _options.Host,
                ClientCertificates = _options.ClientCertificate is { } certificate ? [certificate] : null,
                EnabledSslProtocols = Options.EnabledSslProtocols,
                CertificateRevocationCheckMode = Options.CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = ValidateServerCertificate,
            }, timeout.Token).ConfigureAwait(false);
            return ssl;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException($"The TLS handshake did not complete within {Options.TlsHandshakeTimeout}.");
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (_options.ServerCertificateValidation is { } custom)
            return custom(sender, certificate, chain, errors);

        if (_pinnedCertificateHash is not null)
            return certificate is not null && certificate.GetCertHash(HashAlgorithmName.SHA256).AsSpan().SequenceEqual(_pinnedCertificateHash);

        return errors == SslPolicyErrors.None;
    }

    /// <summary>Sends <paramref name="message"/> to the server.</summary>
    /// <exception cref="InvalidOperationException">Not connected, the type isn't registered, or the message is too large.</exception>
    /// <exception cref="IOException">The write failed; the connection has been closed.</exception>
    public async ValueTask SendAsync(FlowObject message, CancellationToken cancellationToken = default)
    {
        var connection = _connection;
        if (connection is null || !connection.IsConnected)
            throw new InvalidOperationException("The client is not connected. Call ConnectAsync first.");
        await connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the connection and waits for the current handler to finish.
    /// When called from inside one of this client's handlers it doesn't wait (that would deadlock).
    /// </summary>
    public async Task DisconnectAsync()
    {
        var connection = _connection;
        var readLoop = _readLoop;
        if (connection is null)
            return;

        connection.Close();
        if (readLoop is not null && CurrentDispatcher.Value != this)
            await readLoop.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);
    }
}
