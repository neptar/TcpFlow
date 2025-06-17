using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace TcpFlow;

public class TcpFlowServer:TcpFlowBase
{
    List<(TcpClient Client, Stream Stream)> _clients = [];

    public override TcpFlowServer UseSsl(X509Certificate2 serverCertificate)
    {
        base.UseSsl(serverCertificate);
        return this;
    }
    
    public void Listen()
    {
        //HelloWorldGenerated.HelloWorld.SayHello();
        TcpListener listener = TcpListener.Create(Port);
        listener.Start();
        Debug.WriteLine("Server is listening");
        Task.Run(()=>HandleClients(listener));
    }

    private Task HandleClients(TcpListener listener)
    {
        while (Running)
        {
            var client = listener.AcceptTcpClient();
            
            Debug.WriteLine("Client connected");
            Task.Run(()=>HandleClient(client));
        }
        return Task.CompletedTask;
    }

    private Task HandleClient(TcpClient client)
    {
        Stream stream;
        if (ClientCertificate is not null)
        {
            stream = new SslStream(client.GetStream(), false);
            _clients.Add((client, stream));
            ((SslStream)stream).AuthenticateAsServer(ClientCertificate,
                clientCertificateRequired: false,
                enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: true);
        }
        else
        {
            stream = client.GetStream();
            _clients.Add((client, stream));
        }

        while (Running)
        {
            byte[] lengthPrefix = new byte[4];
            byte[] actionPrefix = new byte[100];
            var lenRead = stream.Read(lengthPrefix, 0, 4);
            if (lenRead == 0)
            {
                Debug.WriteLine("Server lost comms with client");
                break;
            }
            int length = BitConverter.ToInt32(lengthPrefix);
            if (length <= 0)
            {
                //return Task.CompletedTask;
                continue;
            }

            Debug.WriteLine($"Server receiving data from {client.Client.RemoteEndPoint}");
            Debug.WriteLine($"Server received data of length: {length}");
            stream.ReadExactly(actionPrefix, 0, 100);
            
            using var ms = new MemoryStream(actionPrefix);
            using var reader = new BinaryReader(ms);
            string action = reader.ReadString();
            //string action = BitConverter.ToString(actionPrefix);
            if (!string.IsNullOrEmpty(action))
                Debug.WriteLine($"Server received action: {action}");
            byte[] data = new byte[length];
            stream.ReadExactly(data, 0, length);
            var result = TcpFlowTypeManager.Deserialize(action, data);
            Debug.WriteLine($"Server Executing with context: {client.Client.RemoteEndPoint}");
            ActionMap[action].Execute(result, new TcpFlowContext() { ConnectionId = client.Client.RemoteEndPoint.ToString()});//.Execute(result);)
        }
        _clients.Remove((client,stream));
        return Task.CompletedTask;
    }
    
    public void SendAll<T>(T hub) where T : FlowObject
    {
        Debug.WriteLine($"Sending to {_clients.Count} clients");
        foreach (var client in _clients)
        {
            SendActionFromTcpClient<T>(client.Client, hub, client.Stream);
        }
    }
    public void Send<T>(T hub, string connectionID) where T : FlowObject
    {
        //get the connection
        var client = _clients.FirstOrDefault(c => c.Client.Client.RemoteEndPoint.ToString() == connectionID);
        SendActionFromTcpClient<T>(client.Client, hub, client.Stream);;
    }
    
}

