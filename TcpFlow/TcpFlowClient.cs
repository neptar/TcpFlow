using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;


namespace TcpFlow;

public class TcpFlowClient():TcpFlowBase
{
    private TcpClient _client;
    private Stream _stream;
    private bool _useSsl;
    
    private byte[] _lengthPrefix = new byte[4];
    private byte[] _actionPrefix = new byte[100];

    public override TcpFlowClient UseSsl(X509Certificate2 serverCertificate = null)
    {
        base.UseSsl(serverCertificate);
        _useSsl = true;
        return this;
    }
    
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
            if (_useSsl)
            {
                _stream = new SslStream(_client.GetStream(), false,
                    new RemoteCertificateValidationCallback(ValidateServerCertificate));
                var clientCertificates = ClientCertificate is not null
                    ? new X509CertificateCollection(new[] { ClientCertificate })
                    : new X509CertificateCollection();

                ((SslStream)_stream).AuthenticateAsClient(HostName,
                    clientCertificates,
                    SslProtocols.Tls12 | SslProtocols.Tls13,
                    checkCertificateRevocation: true);
            }
            else
            {
                _stream = _client.GetStream();
            }

            Debug.WriteLine("Client beginning run sequence");
            while (Running)
            {
                int lenRead = _stream.Read(_lengthPrefix, 0, 4);
                if (lenRead == 0)
                {
                    Debug.WriteLine("Client Lost connection");
                    break;
                }
                int length = BitConverter.ToInt32(_lengthPrefix);
                if (length <= 0)
                {
                    //return Task.CompletedTask;
                    continue;
                }

                Debug.WriteLine($"Client receiving date from {_client.Client.RemoteEndPoint}");
                Debug.WriteLine($"Client received data of length: {length}");
                _stream.ReadExactly(_actionPrefix, 0, 100);

                using var ms = new MemoryStream(_actionPrefix);
                using var reader = new BinaryReader(ms);
                string action = reader.ReadString();
                //string action = BitConverter.ToString(actionPrefix);
                if (!string.IsNullOrEmpty(action))
                    Debug.WriteLine($"Client received action: {action}");
                byte[] data = new byte[length];
                _stream.ReadExactly(data, 0, length);
                var result = TcpFlowTypeManager.Deserialize(action, data);
                Debug.WriteLine($"Client Executing with context: {_client.Client.RemoteEndPoint}");
                ActionMap[action].Execute(result, new TcpFlowContext() { ConnectionId = _client.Client.RemoteEndPoint.ToString()}); //.Execute(result);)
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }

        return Task.CompletedTask;
    }
    
    private bool ValidateServerCertificate(
        object sender, 
        X509Certificate certificate, 
        X509Chain chain, 
        SslPolicyErrors sslPolicyErrors)
    {
        if (ClientCertificate is null)
            return true;
        
        Debug.WriteLine(certificate.GetPublicKeyString());
        Debug.WriteLine(ClientCertificate.GetPublicKeyString());
        Debug.WriteLine(chain.ChainElements.First().Certificate.GetPublicKeyString());
        
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;
        return false; // In production, validate certificate properly
    }

    public void Send<T>(T hub) where T : FlowObject
    {
        SendActionFromTcpClient<T>(_client, hub, _stream);
    }
}