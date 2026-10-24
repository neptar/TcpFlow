using System.Net;
using System.Collections.Concurrent;
using System.Net.Sockets;
using static TcpFlow.Tests.TestHelpers;

namespace TcpFlow.Tests;

public class ConnectionTests
{
    [Fact]
    public async Task Client_sends_and_server_replies_through_the_context()
    {
        await using var server = StartServer(s => s.Map<Ping>((ping, ctx) =>
            ctx.ReplyAsync(new Pong { Sequence = ping.Sequence + 1 }).AsTask()));

        var pong = new TaskCompletionSource<Pong>();
        await using var client = await ConnectAsync(server, c => c.Map<Pong>((p, _) => pong.TrySetResult(p)));

        await client.SendAsync(new Ping { Sequence = 41 });

        Assert.Equal(42, (await pong.WithTimeout()).Sequence);
    }

    [Fact]
    public async Task Server_can_push_to_a_specific_connection()
    {
        await using var server = StartServer();
        var first = new TaskCompletionSource<Ping>();
        var second = new TaskCompletionSource<Ping>();
        await using var clientOne = await ConnectAsync(server, c => c.Map<Ping>((p, _) => first.TrySetResult(p)));
        await using var clientTwo = await ConnectAsync(server, c => c.Map<Ping>((p, _) => second.TrySetResult(p)));
        await WaitForConnectionsAsync(server, 2);

        // The client socket is dual-mode (::ffff:127.0.0.1) while the server is IPv4, so match on the port.
        var clientPort = ((IPEndPoint)clientTwo.Connection!.LocalEndPoint!).Port;
        var target = server.Connections.Single(c => ((IPEndPoint)c.RemoteEndPoint!).Port == clientPort);
        await server.SendAsync(target.Id, new Ping { Text = "just you" });

        Assert.Equal("just you", (await second.WithTimeout()).Text);
        Assert.False(first.Task.IsCompleted);
    }

    [Fact]
    public async Task Sending_to_an_unknown_connection_throws()
    {
        await using var server = StartServer();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => server.SendAsync(999, new Ping()).AsTask());
    }

    [Fact]
    public async Task Broadcast_reaches_every_client_matching_the_filter()
    {
        await using var server = StartServer();
        var received = new ConcurrentBag<int>();
        var clients = new List<TcpFlowClient>();
        var allReceived = new CountdownEvent(2);
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            clients.Add(await ConnectAsync(server, c => c.Map<Ping>((_, _) =>
            {
                received.Add(index);
                allReceived.Signal();
            })));
        }
        await WaitForConnectionsAsync(server, 3);

        var excluded = server.Connections.OrderBy(c => c.Id).First().Id;
        var sent = await server.BroadcastAsync(new Ping(), c => c.Id != excluded);

        Assert.Equal(2, sent);
        Assert.True(allReceived.Wait(TestTimeout));
        Assert.Equal([1, 2], received.Order());
        foreach (var client in clients)
            await client.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_sends_on_one_connection_arrive_intact()
    {
        const int senders = 8, perSender = 250;
        var received = new ConcurrentDictionary<int, string>();
        var done = new TaskCompletionSource();
        await using var server = StartServer(s => s.Map<Ping>((ping, _) =>
        {
            Assert.True(received.TryAdd(ping.Sequence, ping.Text));
            if (received.Count == senders * perSender)
                done.TrySetResult();
        }));
        await using var client = await ConnectAsync(server);

        await Task.WhenAll(Enumerable.Range(0, senders).Select(sender => Task.Run(async () =>
        {
            for (var i = 0; i < perSender; i++)
            {
                var sequence = sender * perSender + i;
                await client.SendAsync(new Ping { Sequence = sequence, Text = new string('x', sequence % 300) });
            }
        })));

        await done.Task.WaitAsync(TestTimeout);
        Assert.All(received, pair => Assert.Equal(pair.Key % 300, pair.Value.Length));
    }

    [Fact]
    public async Task Server_concurrently_replying_to_many_clients_does_not_mix_frames()
    {
        await using var server = StartServer(s => s.Map<Ping>(async (ping, ctx) =>
        {
            for (var i = 0; i < 50; i++)
                await ctx.ReplyAsync(new Pong { Sequence = ping.Sequence * 1000 + i });
        }));

        var clients = new List<(TcpFlowClient Client, ConcurrentQueue<int> Received, TaskCompletionSource Done)>();
        for (var c = 0; c < 6; c++)
        {
            var queue = new ConcurrentQueue<int>();
            var done = new TaskCompletionSource();
            var client = await ConnectAsync(server, cl => cl.Map<Pong>((pong, _) =>
            {
                queue.Enqueue(pong.Sequence);
                if (queue.Count == 50)
                    done.TrySetResult();
            }));
            clients.Add((client, queue, done));
        }

        await Task.WhenAll(clients.Select((c, i) => c.Client.SendAsync(new Ping { Sequence = i }).AsTask()));

        for (var i = 0; i < clients.Count; i++)
        {
            await clients[i].Done.Task.WaitAsync(TestTimeout);
            Assert.Equal(Enumerable.Range(0, 50).Select(n => i * 1000 + n), clients[i].Received);
            await clients[i].Client.DisposeAsync();
        }
    }

    [Fact]
    public async Task Frame_split_across_many_reads_is_reassembled()
    {
        var received = new TaskCompletionSource<Ping>();
        await using var server = StartServer(s => s.Map<Ping>((p, _) => received.TrySetResult(p)));
        using var raw = await ConnectRawAsync(server);
        var stream = raw.GetStream();

        foreach (var b in Frame(new Ping { Sequence = 7, Text = "one byte at a time" }))
        {
            await stream.WriteAsync(new[] { b });
            await stream.FlushAsync();
            await Task.Delay(1);
        }

        var ping = await received.WithTimeout();
        Assert.Equal(7, ping.Sequence);
        Assert.Equal("one byte at a time", ping.Text);
    }

    [Fact]
    public async Task Unknown_type_id_is_skipped_and_the_connection_survives()
    {
        var received = new TaskCompletionSource<Ping>();
        await using var server = StartServer(s => s.Map<Ping>((p, _) => received.TrySetResult(p)));
        using var raw = await ConnectRawAsync(server);

        await raw.GetStream().WriteAsync(Frame(0xDEADBEEF, [1, 2, 3, 4, 5]));
        await raw.GetStream().WriteAsync(Frame(new Ping { Sequence = 3 }));

        Assert.Equal(3, (await received.WithTimeout()).Sequence);
    }

    [Fact]
    public async Task Unmapped_type_is_skipped_and_the_connection_survives()
    {
        var received = new TaskCompletionSource<Ping>();
        await using var server = StartServer(s => s.Map<Ping>((p, _) => received.TrySetResult(p)));
        await using var client = await ConnectAsync(server);

        await client.SendAsync(new Blob { Data = [1, 2, 3] });
        await client.SendAsync(new Ping { Sequence = 5 });

        Assert.Equal(5, (await received.WithTimeout()).Sequence);
    }

    [Theory]
    [InlineData(1024 * 1024)] // larger than the server's 1 KiB limit
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(2)] // shorter than the type id
    public async Task Invalid_frame_length_closes_the_connection(int length)
    {
        var disconnected = new TaskCompletionSource();
        await using var server = StartServer(s => s.ClientDisconnected += (_, _) => disconnected.TrySetResult(), maxMessageSize: 1024);
        using var raw = await ConnectRawAsync(server);
        await WaitForConnectionsAsync(server, 1);

        var header = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await raw.GetStream().WriteAsync(header);

        await disconnected.Task.WaitAsync(TestTimeout);
        Assert.Equal(0, await raw.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TestTimeout));
        Assert.Empty(server.Connections);
    }

    [Fact]
    public async Task Client_disconnecting_mid_frame_is_cleaned_up_and_server_keeps_accepting()
    {
        var disconnected = new TaskCompletionSource();
        var received = new TaskCompletionSource<Ping>();
        await using var server = StartServer(s =>
        {
            s.ClientDisconnected += (_, _) => disconnected.TrySetResult();
            s.Map<Ping>((p, _) => received.TrySetResult(p));
        });

        using (var raw = await ConnectRawAsync(server))
        {
            await WaitForConnectionsAsync(server, 1);
            var frame = Frame(new Ping { Text = "never finished" });
            await raw.GetStream().WriteAsync(frame.AsMemory(0, frame.Length / 2));
        }

        await disconnected.Task.WaitAsync(TestTimeout);
        Assert.Empty(server.Connections);

        await using var client = await ConnectAsync(server);
        await client.SendAsync(new Ping { Sequence = 1 });
        Assert.Equal(1, (await received.WithTimeout()).Sequence);
    }

    [Fact]
    public async Task Handler_exception_does_not_close_the_connection()
    {
        var received = new TaskCompletionSource<Ping>();
        await using var server = StartServer(s => s.Map<Ping>((p, _) =>
        {
            if (p.Sequence == 0)
                throw new InvalidOperationException("boom");
            received.TrySetResult(p);
        }));
        await using var client = await ConnectAsync(server);

        await client.SendAsync(new Ping { Sequence = 0 });
        await client.SendAsync(new Ping { Sequence = 1 });

        Assert.Equal(1, (await received.WithTimeout()).Sequence);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Large_messages_round_trip()
    {
        var received = new TaskCompletionSource<Blob>();
        await using var server = StartServer(s => s.Map<Blob>((b, _) => received.TrySetResult(b)));
        await using var client = await ConnectAsync(server);
        var data = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(data);

        await client.SendAsync(new Blob { Data = data });

        Assert.Equal(data, (await received.WithTimeout()).Data);
    }

    [Fact]
    public async Task Sending_a_message_over_the_size_limit_throws_and_keeps_the_connection()
    {
        await using var server = StartServer();
        await using var client = new TcpFlowClient(new TcpFlowClientOptions
        {
            Host = "127.0.0.1",
            Port = server.LocalEndPoint!.Port,
            MaxMessageSize = 100,
        });
        await client.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new Blob { Data = new byte[200] }).AsTask());
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Sending_before_connecting_throws()
    {
        await using var client = new TcpFlowClient("127.0.0.1", 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new Ping()).AsTask());
    }

    [Fact]
    public async Task Connecting_to_a_closed_port_throws()
    {
        int port;
        using (var listener = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        await using var client = new TcpFlowClient("127.0.0.1", port);
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task Stopping_the_server_disconnects_clients()
    {
        var server = StartServer();
        var disconnected = new TaskCompletionSource();
        await using var client = await ConnectAsync(server, c => c.Disconnected += (_, _) => disconnected.TrySetResult());
        await WaitForConnectionsAsync(server, 1);

        await server.StopAsync();

        await disconnected.Task.WaitAsync(TestTimeout);
        Assert.False(client.IsConnected);
        Assert.False(server.IsRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new Ping()).AsTask());
    }

    [Fact]
    public async Task Server_can_stop_from_inside_a_handler_without_deadlocking()
    {
        TcpFlowServer? server = null;
        var stopped = new TaskCompletionSource();
        server = StartServer(s => s.Map<Ping>(async (_, _) =>
        {
            await server!.StopAsync();
            stopped.TrySetResult();
        }));
        await using var client = await ConnectAsync(server);

        await client.SendAsync(new Ping());

        await stopped.Task.WaitAsync(TestTimeout);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Client_can_reconnect_after_disconnecting()
    {
        var received = new ConcurrentQueue<int>();
        var both = new TaskCompletionSource();
        await using var server = StartServer(s => s.Map<Ping>((p, _) =>
        {
            received.Enqueue(p.Sequence);
            if (received.Count == 2)
                both.TrySetResult();
        }));
        await using var client = await ConnectAsync(server);

        await client.SendAsync(new Ping { Sequence = 1 });
        await client.DisconnectAsync();
        Assert.False(client.IsConnected);
        await client.ConnectAsync();
        await client.SendAsync(new Ping { Sequence = 2 });

        // The two messages travel on different server-side connections, which are handled independently,
        // so only their arrival is guaranteed, not their relative order.
        await both.Task.WaitAsync(TestTimeout);
        Assert.Equal([1, 2], received.Order());
    }

    [Fact]
    public async Task Connected_and_disconnected_events_fire()
    {
        var connected = new TaskCompletionSource<TcpFlowConnection>();
        var disconnected = new TaskCompletionSource<TcpFlowConnection>();
        await using var server = StartServer(s =>
        {
            s.ClientConnected += (_, c) => connected.TrySetResult(c);
            s.ClientDisconnected += (_, c) => disconnected.TrySetResult(c);
        });

        var client = await ConnectAsync(server);
        var connection = await connected.WithTimeout();
        await client.DisposeAsync();

        Assert.Same(connection, await disconnected.WithTimeout());
        Assert.False(connection.IsConnected);
    }
}
