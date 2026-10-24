using System.Globalization;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TcpFlow.Example.Demos;

/// <summary>The basics: map a handler on the server, send from a client, reply through the context.</summary>
public static class CommandsDemo
{
    public static async Task RunAsync(ILoggerFactory loggerFactory)
    {
        // Port 0 picks a free port; LocalEndPoint tells us which one.
        await using var server = new TcpFlowServer(new TcpFlowServerOptions
        {
            Port = 0,
            Address = IPAddress.Loopback,
            LoggerFactory = loggerFactory,
        });
        server.Map<Command>(HandleCommandAsync);
        server.Start();

        // Responses arrive on the client's read loop; a channel hands them to this method.
        var responses = Channel.CreateUnbounded<CommandResponse>();
        await using var client = new TcpFlowClient(new TcpFlowClientOptions
        {
            Host = "localhost",
            Port = server.LocalEndPoint!.Port,
            LoggerFactory = loggerFactory,
        });
        client.Map<CommandResponse>((response, _) => responses.Writer.TryWrite(response));
        await client.ConnectAsync();

        foreach (var (cmd, arg) in new[] { ("echo", "hello over TCP"), ("ls", null), ("cat", null), ("time", null), ("rm", "-rf /") })
        {
            await client.SendAsync(new Command { Cmd = cmd, Arg = arg });
            var response = await responses.Reader.ReadAsync();
            Console.WriteLine($"> {cmd} {arg}".TrimEnd());
            Console.WriteLine(Indent(response.Message));
        }
    }

    public static Task HandleCommandAsync(Command command, TcpFlowContext context)
    {
        var response = command.Cmd.ToLowerInvariant() switch
        {
            "ls" => "passwords.txt\ncat.gif\nalien.mp4",
            "cat" => "meow",
            "echo" => command.Arg ?? "",
            "time" => DateTime.Now.ToString(CultureInfo.InvariantCulture),
            _ => $"Unknown command '{command.Cmd}'",
        };

        // ReplyAsync sends on the connection the command arrived on.
        return context.ReplyAsync(new CommandResponse { Message = response }).AsTask();
    }

    private static string Indent(string text) => "  " + text.Replace("\n", "\n  ");
}
