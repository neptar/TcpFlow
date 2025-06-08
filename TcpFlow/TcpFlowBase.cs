using System.Net;
using System.Net.Sockets;
using TcpFlow.ProtobufHelpers;

namespace TcpFlow;

public class TcpFlowBase
{
    public int Port { get; init; }
    public string HostName { get; init; }
    public IPAddress Ip { get; init; }
    
    //public Dictionary<Type, Action<EasyTcpHub>> ActionMap { get; } = [];
    public Dictionary<string, IHubAction> ActionMap { get; } = [];
    public bool Running { get; private set; } = true;

    private byte[] _lengthPrefix = new byte[4];
    private byte[] _actionPrefix = new byte[100];
    
    public void Map<T>(Action<T, TcpClient> hub) where T : TcpFlowHub
    {
        ActionMap.Add(typeof(T).Name, new ActionWrapper<T>(hub));
    }

    public virtual void SendActionFromTcpClient<T>(TcpClient client, TcpFlowHub hub) where T : TcpFlowHub
    {
        //inject context into hub
        Console.WriteLine($"Client context = {client.Client.RemoteEndPoint.ToString()}");
        hub.Context = new TcpFlowContext() { ConnectionId = client.Client.RemoteEndPoint.ToString() };
        NetworkStream stream = client.GetStream();
        var bytes = Proto.Serialize(hub);
        stream.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
        //byte[] action = new byte[100];
        using var ms = new MemoryStream(_actionPrefix);
        using var writer = new BinaryWriter(ms);
        writer.Write(typeof(T).Name);
        stream.Write(_actionPrefix, 0, 100);
        stream.Write(bytes, 0, bytes.Length);

    }
    
    
}