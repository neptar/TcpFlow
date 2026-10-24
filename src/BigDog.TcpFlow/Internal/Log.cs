using System.Net;
using Microsoft.Extensions.Logging;

namespace TcpFlow.Internal;

internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "Listening on {EndPoint}")]
    public static partial void ServerListening(ILogger logger, EndPoint? endPoint);

    [LoggerMessage(2, LogLevel.Information, "Connection #{ConnectionId} opened from {RemoteEndPoint}")]
    public static partial void ConnectionOpened(ILogger logger, long connectionId, EndPoint? remoteEndPoint);

    [LoggerMessage(3, LogLevel.Information, "Connection #{ConnectionId} to {RemoteEndPoint} closed")]
    public static partial void ConnectionClosed(ILogger logger, long connectionId, EndPoint? remoteEndPoint);

    [LoggerMessage(4, LogLevel.Warning, "Could not set up connection #{ConnectionId} from {RemoteEndPoint}")]
    public static partial void ConnectionSetupFailed(ILogger logger, long connectionId, EndPoint? remoteEndPoint, Exception exception);

    [LoggerMessage(5, LogLevel.Warning, "Accepting a connection failed")]
    public static partial void AcceptFailed(ILogger logger, Exception exception);

    [LoggerMessage(6, LogLevel.Information, "Connected to {Host}:{Port}")]
    public static partial void Connected(ILogger logger, string host, int port);

    [LoggerMessage(10, LogLevel.Warning, "Connection #{ConnectionId}: invalid frame length {Length} (max payload {MaxMessageSize}); closing")]
    public static partial void InvalidFrameLength(ILogger logger, long connectionId, int length, int maxMessageSize);

    [LoggerMessage(11, LogLevel.Debug, "Connection #{ConnectionId}: peer disconnected in the middle of a frame")]
    public static partial void TruncatedFrame(ILogger logger, long connectionId);

    [LoggerMessage(12, LogLevel.Debug, "Connection #{ConnectionId}: I/O error")]
    public static partial void ConnectionError(ILogger logger, long connectionId, Exception exception);

    [LoggerMessage(13, LogLevel.Warning, "Connection #{ConnectionId}: unknown flow type id 0x{TypeId:X8}; message skipped")]
    public static partial void UnknownType(ILogger logger, long connectionId, uint typeId);

    [LoggerMessage(14, LogLevel.Warning, "Connection #{ConnectionId}: no handler mapped for {TypeName}; message skipped")]
    public static partial void NoHandler(ILogger logger, long connectionId, string typeName);

    [LoggerMessage(15, LogLevel.Error, "Connection #{ConnectionId}: could not deserialize {TypeName}; message skipped")]
    public static partial void DeserializationFailed(ILogger logger, long connectionId, string typeName, Exception exception);

    [LoggerMessage(16, LogLevel.Error, "Connection #{ConnectionId}: handler for {TypeName} threw")]
    public static partial void HandlerFailed(ILogger logger, long connectionId, string typeName, Exception exception);

    [LoggerMessage(17, LogLevel.Warning, "Broadcast to connection #{ConnectionId} failed")]
    public static partial void BroadcastFailed(ILogger logger, long connectionId, Exception exception);

    [LoggerMessage(18, LogLevel.Error, "A connection event handler threw")]
    public static partial void EventHandlerFailed(ILogger logger, Exception exception);
}
