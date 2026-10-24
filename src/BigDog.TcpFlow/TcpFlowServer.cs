using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using BigDog.TcpFlow.Internal;

namespace BigDog.TcpFlow;

/// <summary>Accepts TcpFlow connections and dispatches incoming messages to mapped handlers.</summary>
public sealed class TcpFlowServer : TcpFlowEndpoint
{
    private const int StateNew = 0, StateRunning = 1, StateStopped = 2;

    private readonly TcpFlowServerOptions _options;
    private readonly ConcurrentDictionary<long, TcpFlowConnection> _connections = new();
    private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private long _lastConnectionId;
    private int _state;

    /// <summary>Creates a server listening on <paramref name="port"/> on all interfaces, with default options.</summary>
    public TcpFlowServer(int port) : this(new TcpFlowServerOptions { Port = port })
    {
    }

    /// <summary>Creates a server. Call <see cref="Start"/> to begin accepting connections.</summary>
    public TcpFlowServer(TcpFlowServerOptions options) : base(options, "BigDog.TcpFlow.TcpFlowServer")
    {
        _options = options;
    }

    /// <summary>Raised after a client connects (and completes TLS, if enabled).</summary>
    public event EventHandler<TcpFlowConnection>? ClientConnected;

    /// <summary>Raised after a client connection closes, for any reason.</summary>
    public event EventHandler<TcpFlowConnection>? ClientDisconnected;

    /// <summary>The bound address and port, once started.</summary>
    public IPEndPoint? LocalEndPoint => _listener?.LocalEndpoint as IPEndPoint;

    /// <summary>Whether the server is accepting connections.</summary>
    public bool IsRunning => Volatile.Read(ref _state) == StateRunning;

    /// <summary>A snapshot of the open connections.</summary>
    public IReadOnlyCollection<TcpFlowConnection> Connections => _connections.Values.ToArray();

    /// <summary>Binds the port and starts accepting connections in the background.</summary>
    /// <exception cref="InvalidOperationException">The server was already started or stopped.</exception>
    /// <exception cref="SocketException">The port could not be bound.</exception>
    public void Start()
    {
        var previous = Interlocked.CompareExchange(ref _state, StateRunning, StateNew);
        if (previous != StateNew)
            throw new InvalidOperationException(previous == StateRunning ? "The server is already running." : "The server has been stopped.");

        var listener = _options.Address is null
            ? TcpListener.Create(_options.Port)
            : new TcpListener(_options.Address, _options.Port);
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Dispose();
            Volatile.Write(ref _state, StateNew);
            throw;
        }

        _listener = listener;
        Log.ServerListening(Logger, LocalEndPoint);
        _acceptLoop = AcceptLoopAsync(listener, _shutdown.Token);
    }

    /// <summary>Returns the open connection with the given id.</summary>
    public bool TryGetConnection(long connectionId, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TcpFlowConnection connection)
        => _connections.TryGetValue(connectionId, out connection);

    /// <summary>Sends <paramref name="message"/> to one connection.</summary>
    /// <exception cref="KeyNotFoundException">No open connection has that id.</exception>
    public ValueTask SendAsync(long connectionId, FlowObject message, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
            throw new KeyNotFoundException($"No open connection with id {connectionId}.");
        return connection.SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="message"/> to every open connection (or those matching <paramref name="filter"/>).
    /// The message is serialized once. A failure on one connection is logged and doesn't affect the others.
    /// </summary>
    /// <returns>The number of connections the message was written to.</returns>
    public async Task<int> BroadcastAsync(FlowObject message, Func<TcpFlowConnection, bool>? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var frame = SerializeFrame(message);
        var bytes = frame.WrittenMemory;

        var targets = _connections.Values.Where(c => c.IsConnected && (filter is null || filter(c))).ToArray();
        if (targets.Length == 0)
            return 0;

        var results = await Task.WhenAll(targets.Select(c => TryWriteFrameAsync(c, bytes, cancellationToken))).ConfigureAwait(false);
        return results.Count(sent => sent);
    }

    private async Task<bool> TryWriteFrameAsync(TcpFlowConnection connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        try
        {
            await connection.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.BroadcastFailed(Logger, connection.Id, ex);
            return false;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException || token.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.AcceptFailed(Logger, ex);
                continue;
            }

            var id = Interlocked.Increment(ref _lastConnectionId);
            var task = HandleClientAsync(id, client, token);
            _clientTasks[id] = task;
            _ = task.ContinueWith(_ => _clientTasks.TryRemove(id, out Task? _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(long id, TcpClient client, CancellationToken token)
    {
        // Return to the accept loop straight away; the TLS handshake and read loop run on the thread pool.
        await Task.Yield();

        EndPoint? remote = null;
        TcpFlowConnection? connection = null;
        try
        {
            remote = client.Client.RemoteEndPoint;
            client.NoDelay = Options.NoDelay;
            var stream = await AuthenticateAsync(client, token).ConfigureAwait(false);

            // Only registered after TLS completes, so broadcasts never write to a half-open connection.
            connection = new TcpFlowConnection(id, client, stream, this, token);
            _connections[id] = connection;
            Log.ConnectionOpened(Logger, id, remote);
            RaiseEvent(ClientConnected, connection);

            await connection.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.ConnectionSetupFailed(Logger, id, remote, ex);
        }
        finally
        {
            if (connection is null)
            {
                client.Dispose();
            }
            else
            {
                connection.Close();
                _connections.TryRemove(id, out _);
                Log.ConnectionClosed(Logger, id, connection.RemoteEndPoint);
                RaiseEvent(ClientDisconnected, connection);
            }
        }
    }

    private async Task<Stream> AuthenticateAsync(TcpClient client, CancellationToken token)
    {
        var network = client.GetStream();
        if (_options.ServerCertificate is null)
            return network;

        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(Options.TlsHandshakeTimeout);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _options.ServerCertificate,
                ClientCertificateRequired = _options.RequireClientCertificate,
                RemoteCertificateValidationCallback = _options.ClientCertificateValidation,
                EnabledSslProtocols = Options.EnabledSslProtocols,
                CertificateRevocationCheckMode = Options.CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
            }, timeout.Token).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Stops accepting, closes every connection and waits for in-flight handlers to finish.
    /// When called from inside one of this server's handlers it doesn't wait (that would deadlock).
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _state, StateStopped) != StateRunning)
            return;

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        foreach (var connection in _connections.Values)
            connection.Close();

        if (CurrentDispatcher.Value == this)
            return;

        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(false);
        await Task.WhenAll(_clientTasks.Values).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _listener?.Dispose();
    }
}
