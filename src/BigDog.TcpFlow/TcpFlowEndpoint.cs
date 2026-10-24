using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TcpFlow.Internal;
using TcpFlow.Serializer;

namespace TcpFlow;

/// <summary>Functionality shared by <see cref="TcpFlowServer"/> and <see cref="TcpFlowClient"/>: handlers, framing and dispatch.</summary>
public abstract class TcpFlowEndpoint : IAsyncDisposable
{
    internal static readonly AsyncLocal<TcpFlowEndpoint?> CurrentDispatcher = new();

    private readonly ConcurrentDictionary<Type, Func<FlowObject, TcpFlowContext, ValueTask>> _handlers = new();

    private protected TcpFlowEndpoint(TcpFlowOptions options, string loggerCategory)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        Logger = options.LoggerFactory.CreateLogger(loggerCategory);
    }

    internal TcpFlowOptions Options { get; }

    internal ILogger Logger { get; }

    /// <summary>The flow types this endpoint can send and receive.</summary>
    public FlowTypeRegistry Registry => Options.Registry;

    /// <summary>The serializer used for every message.</summary>
    public IFlowSerializer Serializer => Options.Serializer;

    /// <summary>Handles incoming <typeparamref name="T"/> messages. Only one handler per type.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> isn't registered or already has a handler.</exception>
    public void Map<T>(Action<T, TcpFlowContext> handler) where T : FlowObject
    {
        ArgumentNullException.ThrowIfNull(handler);
        AddHandler(typeof(T), (message, context) =>
        {
            handler((T)message, context);
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Handles incoming <typeparamref name="T"/> messages asynchronously. Only one handler per type.</summary>
    /// <remarks>
    /// Messages from one connection are handled one at a time, so the next message waits until the returned task completes.
    /// Hand long-running work off (e.g. to a <see cref="System.Threading.Channels.Channel"/>) if that ordering isn't needed.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> isn't registered or already has a handler.</exception>
    public void Map<T>(Func<T, TcpFlowContext, Task> handler) where T : FlowObject
    {
        ArgumentNullException.ThrowIfNull(handler);
        AddHandler(typeof(T), (message, context) => new ValueTask(handler((T)message, context)));
    }

    /// <summary>Removes the handler for <typeparamref name="T"/>, if any.</summary>
    public bool Unmap<T>() where T : FlowObject => _handlers.TryRemove(typeof(T), out _);

    private void AddHandler(Type type, Func<FlowObject, TcpFlowContext, ValueTask> handler)
    {
        if (!Registry.Contains(type))
            throw new InvalidOperationException(
                $"'{type.FullName}' is not a registered flow type. It must derive from FlowObject, be non-abstract and non-generic, " +
                "and be declared in a project that runs the TcpFlow source generator.");
        if (!_handlers.TryAdd(type, handler))
            throw new InvalidOperationException($"A handler for '{type.FullName}' is already mapped.");
    }

    /// <summary>Serializes <paramref name="message"/> into a complete frame. The caller disposes the result.</summary>
    internal PooledBufferWriter SerializeFrame(FlowObject message)
    {
        var type = message.GetType();
        if (!Registry.TryGet(type, out var info))
            throw new InvalidOperationException($"'{type.FullName}' is not a registered flow type.");

        var writer = new PooledBufferWriter();
        try
        {
            writer.GetSpan(FrameHeader.Size);
            writer.Advance(FrameHeader.Size);
            info.Serialize(Serializer, writer, message);

            var payloadLength = writer.WrittenCount - FrameHeader.Size;
            if (payloadLength > Options.MaxMessageSize)
                throw new InvalidOperationException(
                    $"'{info.Name}' serialized to {payloadLength} bytes, more than MaxMessageSize ({Options.MaxMessageSize}).");

            FrameHeader.Write(writer.WrittenSpan, payloadLength, info.Id);
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    internal async ValueTask DispatchAsync(TcpFlowConnection connection, uint typeId, ReadOnlyMemory<byte> payload)
    {
        // Frames are length-delimited, so anything we can't handle is skipped without losing sync with the stream.
        if (!Registry.TryGet(typeId, out var info))
        {
            Log.UnknownType(Logger, connection.Id, typeId);
            return;
        }

        if (!_handlers.TryGetValue(info.Type, out var handler))
        {
            Log.NoHandler(Logger, connection.Id, info.Name);
            return;
        }

        FlowObject message;
        try
        {
            message = info.Deserialize(Serializer, payload.Span);
        }
        catch (Exception ex)
        {
            Log.DeserializationFailed(Logger, connection.Id, info.Name, ex);
            return;
        }

        var context = connection.Context;
        try
        {
            await handler(message, context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.HandlerFailed(Logger, connection.Id, info.Name, ex);
        }
    }

    private protected void RaiseEvent(EventHandler<TcpFlowConnection>? handler, TcpFlowConnection connection)
    {
        if (handler is null)
            return;
        try
        {
            handler(this, connection);
        }
        catch (Exception ex)
        {
            Log.EventHandlerFailed(Logger, ex);
        }
    }

    /// <summary>Stops the endpoint and closes its connections.</summary>
    public abstract ValueTask DisposeAsync();
}
