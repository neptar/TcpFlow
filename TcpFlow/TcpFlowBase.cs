using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using TcpFlow.Serializer;

namespace TcpFlow;

public class TcpFlowBase
{
    public int Port { get; init; }
    public string HostName { get; init; }
    public IPAddress Ip { get; init; }
    
    //public Dictionary<Type, Action<EasyTcpHub>> ActionMap { get; } = [];
    public Dictionary<string, IHubAction> ActionMap { get; } = [];
    public IFlowSerializer Serializer { get; init; } = new JsonFlowSerializer();
    public bool Running { get; private set; } = true;

    private byte[] _lengthPrefix = new byte[4];
    private byte[] _actionPrefix = new byte[100];
    private X509Certificate2? _clientCertificate;
    public X509Certificate2? ClientCertificate  => _clientCertificate;

    public virtual TcpFlowBase UseSsl(X509Certificate2 serverCertificate)
    {
        _clientCertificate = serverCertificate;
        return this;
    }
    
    public void Map<T>(Action<T, TcpFlowContext> hub) where T : FlowObject
    {
        ActionMap.Add(typeof(T).Name, new ActionWrapper<T>(hub));
    }

    public virtual void SendActionFromTcpClient<T>(TcpClient client, T dataObject, Stream stream) where T : FlowObject
    {
        try
        {
            //inject context into hub
            Debug.WriteLine($"Client context = {client.Client.RemoteEndPoint}");
            var bytes = Serializer.Serialize(dataObject);
            stream.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
            using var ms = new MemoryStream(_actionPrefix);
            using var writer = new BinaryWriter(ms);
            writer.Write(typeof(T).Name);
            stream.Write(_actionPrefix, 0, 100);
            stream.Write(bytes, 0, bytes.Length);
        }catch(Exception e)
        {
            Debug.WriteLine(e.ToString());
        }
    }
    
    
}