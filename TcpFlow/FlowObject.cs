using System.Diagnostics;
using System.Net.Sockets;
namespace TcpFlow;



public abstract class FlowObject;// Base Hub class


public class TcpFlowContext
{
    public required string ConnectionId { get; set; }
}
public interface IHubAction
{
    void Execute(FlowObject flowObject, TcpFlowContext context);
}

public class ActionWrapper<T> : IHubAction where T : FlowObject
{
    private readonly Action<T, TcpFlowContext> _action;

    public ActionWrapper(Action<T, TcpFlowContext> action)
    {
        _action = action;
    }

    public void Execute(FlowObject flowObject, TcpFlowContext context)
    {
        Debug.WriteLine("BANG");
        if (flowObject is T specificHub)
        {
            Debug.WriteLine("Executing action in wrapper");
            _action(specificHub, context);
        }
        else
        {
            Debug.WriteLine($"Hub is not of type {typeof(T).Name}");
            throw new InvalidCastException($"Hub is not of type {typeof(T).Name}");
        }
    }
}