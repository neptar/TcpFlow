using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using MemoryPack;

namespace BigDog.TcpFlow.Tests;

internal static class TestHelpers
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public static TcpFlowServer StartServer(Action<TcpFlowServer>? configure = null, int maxMessageSize = 4 * 1024 * 1024)
    {
        var server = new TcpFlowServer(new TcpFlowServerOptions
        {
            Port = 0,
            Address = IPAddress.Loopback,
            MaxMessageSize = maxMessageSize,
        });
        configure?.Invoke(server);
        server.Start();
        return server;
    }

    public static async Task<TcpFlowClient> ConnectAsync(TcpFlowServer server, Action<TcpFlowClient>? configure = null)
    {
        var client = new TcpFlowClient("127.0.0.1", server.LocalEndPoint!.Port);
        configure?.Invoke(client);
        await client.ConnectAsync();
        return client;
    }

    /// <summary>Opens a raw socket so tests can write arbitrary bytes, bypassing the client.</summary>
    public static async Task<TcpClient> ConnectRawAsync(TcpFlowServer server)
    {
        var raw = new TcpClient { NoDelay = true };
        await raw.ConnectAsync(IPAddress.Loopback, server.LocalEndPoint!.Port);
        return raw;
    }

    /// <summary>Builds a frame by hand, following the documented wire format.</summary>
    public static byte[] Frame<T>(T message) where T : FlowObject
    {
        Assert.True(FlowTypes.Registry.TryGet(typeof(T), out var info));
        return Frame(info.Id, MemoryPackSerializer.Serialize(message));
    }

    public static byte[] Frame(uint typeId, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length + 4);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), typeId);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    /// <summary>Waits until the server reports the expected number of connections.</summary>
    public static async Task WaitForConnectionsAsync(TcpFlowServer server, int count)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (server.Connections.Count != count)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {count} connections, have {server.Connections.Count}.");
            await Task.Delay(10);
        }
    }

    public static Task<T> WithTimeout<T>(this TaskCompletionSource<T> source) => source.Task.WaitAsync(TestTimeout);
}
