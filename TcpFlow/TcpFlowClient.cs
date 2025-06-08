using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using TcpFlow.ProtobufHelpers;

namespace TcpFlow;

public class TcpFlowClient:TcpFlowBase
{
    private TcpClient _client;
    
    public void Start()
    {
        Task.Run(ProcessClient);
    }

    private Task ProcessClient()
    {
        try
        {
            _client = new TcpClient(HostName, Port);
            Debug.WriteLine("Client connected to server");
            using var stream = _client.GetStream();
            while (Running)
            {
                byte[] lengthPrefix = new byte[4];
                byte[] actionPrefix = new byte[100];
                int lenRead = stream.Read(lengthPrefix, 0, 4);
                if (lenRead == 0)
                {
                    Debug.WriteLine("Client Lost connection");
                    break;
                }
                int length = BitConverter.ToInt32(lengthPrefix);
                if (length <= 0)
                {
                    //return Task.CompletedTask;
                    continue;
                }

                Debug.WriteLine($"Client receiving date from {_client.Client.RemoteEndPoint}");
                Debug.WriteLine($"Client received data of length: {length}");
                stream.ReadExactly(actionPrefix, 0, 100);

                using var ms = new MemoryStream(actionPrefix);
                using var reader = new BinaryReader(ms);
                string action = reader.ReadString();
                //string action = BitConverter.ToString(actionPrefix);
                if (!string.IsNullOrEmpty(action))
                    Debug.WriteLine($"Client received action: {action}");
                byte[] data = new byte[length];
                stream.ReadExactly(data, 0, length);
                var result = TcpFlowTypeManager.Deserialize(action, data);
                ActionMap[action].Execute(result, _client); //.Execute(result);)
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.ToString());
        }

        return Task.CompletedTask;
    }

    public void Send<T>(T hub) where T : TcpFlowHub
    {
        SendActionFromTcpClient<T>(_client, hub);
    }
}