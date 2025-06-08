using System.Diagnostics;
using System.Net.Sockets;
using ProtoBuf;

namespace TcpFlow;


[ProtoContract]
public abstract partial class TcpFlowHub
{
    [ProtoMember(1)]
    public TcpFlowContext Context { get; set; }
} // Base Hub class

[ProtoContract]
public class TcpFlowContext
{
    [ProtoMember(1)]
    public required string ConnectionId { get; set; }
}
public interface IHubAction
{
    void Execute(TcpFlowHub tcpFlowHub, TcpClient client);
}

public class ActionWrapper<T> : IHubAction where T : TcpFlowHub
{
    private readonly Action<T, TcpClient> _action;

    public ActionWrapper(Action<T, TcpClient> action)
    {
        _action = action;
    }

    public void Execute(TcpFlowHub tcpFlowHub, TcpClient client)
    {
        Debug.WriteLine("BANG");
        if (tcpFlowHub is T specificHub)
        {
            Debug.WriteLine("Executing action in wrapper");
            _action(specificHub, client);
        }
        else
        {
            Debug.WriteLine($"Hub is not of type {typeof(T).Name}");
            throw new InvalidCastException($"Hub is not of type {typeof(T).Name}");
        }
    }
}