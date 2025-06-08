using System.Diagnostics;
using System.Net.Sockets;
using TcpFlow.ProtobufHelpers;

namespace TcpFlow;

public class TcpFlowServer:TcpFlowBase
{
    List<TcpClient> _clients = [];
    public void Listen()
    {
        //HelloWorldGenerated.HelloWorld.SayHello();
        TcpListener listener = TcpListener.Create(Port);
        listener.Start();
        Console.WriteLine("Server is listening");
        Task.Run(()=>HandleClients(listener));
    }

    private Task HandleClients(TcpListener listener)
    {
        while (Running)
        {
            var client = listener.AcceptTcpClient();
            _clients.Add(client);
            Debug.WriteLine("Client connected");
            Task.Run(()=>HandleClient(client));
        }
        return Task.CompletedTask;
    }

    private Task HandleClient(TcpClient client)
    {
        using var stream = client.GetStream();
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

            Debug.WriteLine($"Server receiving date from {client.Client.RemoteEndPoint}");
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
            ActionMap[action].Execute(result, client);//.Execute(result);)
        }
        return Task.CompletedTask;
    }
    
    public void SendAll<T>(T hub) where T : TcpFlowHub
    {
        Debug.WriteLine($"Sending to {_clients.Count} clients");
        foreach (var client in _clients)
        {
            SendActionFromTcpClient<T>(client, hub);
        }
    }
    public void Send<T>(T hub, TcpClient client) where T : TcpFlowHub
    {
        SendActionFromTcpClient<T>(client, hub);
    }
    
}

