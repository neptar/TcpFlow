namespace TcpFlow;

/// <summary>Passed to every message handler. One instance exists per connection.</summary>
public sealed class TcpFlowContext
{
    internal TcpFlowContext(TcpFlowConnection connection, CancellationToken cancellationToken)
    {
        Connection = connection;
        CancellationToken = cancellationToken;
    }

    /// <summary>The connection the message arrived on.</summary>
    public TcpFlowConnection Connection { get; }

    /// <summary>Shortcut for <c>Connection.Id</c>.</summary>
    public long ConnectionId => Connection.Id;

    /// <summary>Cancelled when the connection closes or the server/client stops.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Sends <paramref name="message"/> back on the connection the current message arrived on.</summary>
    public ValueTask ReplyAsync(FlowObject message) => Connection.SendAsync(message, CancellationToken);
}
