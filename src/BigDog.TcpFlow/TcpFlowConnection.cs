using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using TcpFlow.Internal;

namespace TcpFlow;

/// <summary>One open TCP (or TLS) connection. Safe to send on from multiple threads at once.</summary>
public sealed class TcpFlowConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly TcpFlowEndpoint _endpoint;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts;
    private int _closed;

    internal TcpFlowConnection(long id, TcpClient client, Stream stream, TcpFlowEndpoint endpoint, CancellationToken shutdownToken)
    {
        Id = id;
        _client = client;
        _stream = stream;
        _endpoint = endpoint;
        RemoteEndPoint = client.Client.RemoteEndPoint;
        LocalEndPoint = client.Client.LocalEndPoint;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        Context = new TcpFlowContext(this, _cts.Token);
    }

    /// <summary>An id unique within the server or client that owns this connection.</summary>
    public long Id { get; }

    /// <summary>The peer's address, captured when the connection opened.</summary>
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>This side's address, captured when the connection opened.</summary>
    public EndPoint? LocalEndPoint { get; }

    /// <summary>Whether the connection is still open.</summary>
    public bool IsConnected => Volatile.Read(ref _closed) == 0;

    /// <summary>Whether the connection uses TLS.</summary>
    public bool IsEncrypted => _stream is SslStream;

    /// <summary>The certificate the peer presented, if any.</summary>
    public X509Certificate? RemoteCertificate => (_stream as SslStream)?.RemoteCertificate;

    internal TcpFlowContext Context { get; }

    /// <summary>Serializes and sends <paramref name="message"/>.</summary>
    /// <exception cref="InvalidOperationException">The type isn't registered, the message is too large, or the connection is closed.</exception>
    /// <exception cref="IOException">The write failed; the connection has been closed.</exception>
    public async ValueTask SendAsync(FlowObject message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var frame = _endpoint.SerializeFrame(message);
        await WriteFrameAsync(frame.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (!IsConnected)
            throw new InvalidOperationException($"Connection #{Id} is closed.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A partially written frame leaves the stream unusable, so close rather than corrupt the next frame.
            Close();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads frames and dispatches them until the connection closes. Handlers run one at a time, in order.</summary>
    internal async Task RunAsync()
    {
        // Lets StopAsync/DisconnectAsync detect being called from inside a handler and avoid waiting on itself.
        TcpFlowEndpoint.CurrentDispatcher.Value = _endpoint;

        var token = _cts.Token;
        var logger = _endpoint.Logger;
        var maxMessageSize = _endpoint.Options.MaxMessageSize;
        var header = new byte[FrameHeader.Size];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await _stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (read < header.Length)
                {
                    Log.TruncatedFrame(logger, Id);
                    break;
                }

                if (!FrameHeader.TryRead(header, maxMessageSize, out var length, out var typeId))
                {
                    Log.InvalidFrameLength(logger, Id, length, maxMessageSize);
                    break;
                }

                var payloadLength = FrameHeader.PayloadLength(length);
                var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
                try
                {
                    await _stream.ReadExactlyAsync(payload.AsMemory(0, payloadLength), token).ConfigureAwait(false);
                    await _endpoint.DispatchAsync(this, typeId, payload.AsMemory(0, payloadLength)).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
            Log.TruncatedFrame(logger, Id);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (IsConnected)
                Log.ConnectionError(logger, Id, ex);
        }
        finally
        {
            Close();
            _cts.Dispose();
        }
    }

    /// <summary>Closes the connection. Safe to call more than once and from inside a handler.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _stream.Dispose();
        _client.Dispose();
    }

    /// <summary>Closes the connection.</summary>
    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override string ToString() => $"#{Id} ({RemoteEndPoint})";
}
