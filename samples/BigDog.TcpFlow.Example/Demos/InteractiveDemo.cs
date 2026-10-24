using System.Net;
using Microsoft.Extensions.Logging;

namespace TcpFlow.Example.Demos;

/// <summary>
/// Type commands (ls, cat, echo &lt;text&gt;, time) and see the server's answers. Run it as a single process, or split it:
/// <c>interactive server [port]</c> in one terminal and <c>interactive client [host] [port]</c> in another.
/// </summary>
public static class InteractiveDemo
{
    private const int DefaultPort = 54321;

    public static async Task RunAsync(ILoggerFactory loggerFactory)
    {
        var args = Environment.GetCommandLineArgs().Skip(2).Where(a => !a.StartsWith('-')).ToArray();
        var role = args.ElementAtOrDefault(0) ?? "both";

        switch (role)
        {
            case "server":
                await RunServerAsync(ParsePort(args.ElementAtOrDefault(1)), loggerFactory);
                break;
            case "client":
                await RunClientAsync(args.ElementAtOrDefault(1) ?? "localhost", ParsePort(args.ElementAtOrDefault(2)), loggerFactory);
                break;
            default:
                await using (var server = StartServer(DefaultPort, loggerFactory))
                    await RunClientAsync("localhost", DefaultPort, loggerFactory);
                break;
        }
    }

    private static int ParsePort(string? value) => int.TryParse(value, out var port) ? port : DefaultPort;

    private static TcpFlowServer StartServer(int port, ILoggerFactory loggerFactory)
    {
        var server = new TcpFlowServer(new TcpFlowServerOptions { Port = port, LoggerFactory = loggerFactory });
        server.Map<Command>(CommandsDemo.HandleCommandAsync);
        server.ClientConnected += (_, c) => Console.WriteLine($"[server] client {c} connected");
        server.ClientDisconnected += (_, c) => Console.WriteLine($"[server] client {c} disconnected");
        server.Start();
        Console.WriteLine($"[server] listening on {server.LocalEndPoint}");
        return server;
    }

    private static async Task RunServerAsync(int port, ILoggerFactory loggerFactory)
    {
        await using var server = StartServer(port, loggerFactory);
        Console.WriteLine("Press Ctrl+C to stop.");
        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        await stop.Task;
    }

    private static async Task RunClientAsync(string host, int port, ILoggerFactory loggerFactory)
    {
        await using var client = new TcpFlowClient(new TcpFlowClientOptions { Host = host, Port = port, LoggerFactory = loggerFactory });
        client.Map<CommandResponse>((response, _) => Console.WriteLine(response.Message));
        client.Disconnected += (_, _) => Console.WriteLine("[client] disconnected from server");
        await client.ConnectAsync();
        Console.WriteLine($"[client] connected to {host}:{port}. Try: ls, cat, echo <text>, time. Empty line quits.");

        while (Console.ReadLine() is { Length: > 0 } line && client.IsConnected)
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            await client.SendAsync(new Command { Cmd = parts[0], Arg = parts.ElementAtOrDefault(1) });
        }
    }
}
