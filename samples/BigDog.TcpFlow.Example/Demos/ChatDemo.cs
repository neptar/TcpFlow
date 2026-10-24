using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace BigDog.TcpFlow.Example.Demos;

/// <summary>
/// A chat room: the server keeps per-connection state, relays messages with BroadcastAsync,
/// and uses connection events to announce arrivals and departures.
/// </summary>
public static class ChatDemo
{
    public static async Task RunAsync(ILoggerFactory loggerFactory)
    {
        await using var server = new TcpFlowServer(new TcpFlowServerOptions
        {
            Port = 0,
            Address = IPAddress.Loopback,
            LoggerFactory = loggerFactory,
        });

        // Connection ids are unique per server, so they make good keys for per-client state.
        var userNames = new ConcurrentDictionary<long, string>();

        server.Map<JoinChat>(async (join, ctx) =>
        {
            userNames[ctx.ConnectionId] = join.UserName;
            await ctx.ReplyAsync(new SystemNotice { Text = $"Welcome, {join.UserName}! {userNames.Count} user(s) online." });
            await server.BroadcastAsync(new SystemNotice { Text = $"{join.UserName} joined" }, c => c.Id != ctx.ConnectionId);
        });

        server.Map<ChatMessage>(async (message, ctx) =>
        {
            // Never trust the client's claim of who it is; stamp the name the server knows.
            if (!userNames.TryGetValue(ctx.ConnectionId, out var from))
            {
                await ctx.ReplyAsync(new SystemNotice { Text = "Join first." });
                return;
            }
            await server.BroadcastAsync(new ChatMessage { From = from, Text = message.Text, SentAt = DateTimeOffset.UtcNow },
                c => c.Id != ctx.ConnectionId && userNames.ContainsKey(c.Id));
        });

        server.ClientDisconnected += (_, connection) =>
        {
            if (userNames.TryRemove(connection.Id, out var name))
                _ = server.BroadcastAsync(new SystemNotice { Text = $"{name} left" });
        };

        server.Start();

        var alice = await JoinAsync("alice", server, loggerFactory);
        await Settle();
        var bob = await JoinAsync("bob", server, loggerFactory);
        await Settle();
        var carol = await JoinAsync("carol", server, loggerFactory);
        await Settle();

        await alice.SendAsync(new ChatMessage { Text = "Hi everyone!" });
        await Settle();
        await bob.SendAsync(new ChatMessage { Text = "Hey alice 👋" });
        await Settle();

        await carol.DisposeAsync();
        await Settle();

        await alice.DisposeAsync();
        await bob.DisposeAsync();
    }

    private static async Task<TcpFlowClient> JoinAsync(string name, TcpFlowServer server, ILoggerFactory loggerFactory)
    {
        var client = new TcpFlowClient(new TcpFlowClientOptions
        {
            Host = "localhost",
            Port = server.LocalEndPoint!.Port,
            LoggerFactory = loggerFactory,
        });
        client.Map<SystemNotice>((notice, _) => Console.WriteLine($"[{name}] * {notice.Text}"));
        client.Map<ChatMessage>((message, _) => Console.WriteLine($"[{name}] <{message.From}> {message.Text}"));

        await client.ConnectAsync();
        await client.SendAsync(new JoinChat { UserName = name });
        return client;
    }

    // The demo runs everyone in one process; a short pause keeps the console output in a readable order.
    private static Task Settle() => Task.Delay(100);
}
