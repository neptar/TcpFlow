// See https://aka.ms/new-console-template for more information

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using TcpFlow;
using ProtoBuf;

var server = Builder.CreateServer(54321);
var clientOne = Builder.CreateClient(Dns.GetHostName(), 54321);
var clientTwo = Builder.CreateClient(Dns.GetHostName(), 54321);

//server.Map(new Action<TextMessage>(message => Console.WriteLine($"{message.TimeSent}:{message.Message}")));;
//server.Map(new ActionWrapper<EasyMessage>(message => Console.WriteLine($"{message.Message}")));;

server.Map<TextMessage>((message,_) => Console.WriteLine($"{message.TimeSent}:{message.Message}"));
clientOne.Map<TextMessage>((message,_) => Console.WriteLine($"{message.TimeSent}:{message.Message}"));

server.Map<Command>(HandleCommand);

clientOne.Map<CommandResponse>((message,_) => Console.WriteLine($"{message.Message}"));
clientTwo.Map<CommandResponse>((message,_) => Console.WriteLine($"{message.Message}"));

server.Listen();

clientOne.Start();
clientTwo.Start();

Thread.Sleep(1000);

string cmd;
while (true)
{
    cmd = Console.ReadLine();
    var cmdSplit = cmd.Split(' ');
    clientOne.Send(new Command() { Cmd = cmdSplit[0], Arg = string.Join(' ', cmdSplit.Skip(1)) });
}

//Console.ReadLine();
//Console.ReadLine();

void HandleCommand(Command command, TcpClient client)
{
    string response = command.Cmd.ToLower() switch
    {
        "ls" => @"passwords.txt
cat.gif
alien.mp4",
        "cat" => "meow",
        "echo" => command.Arg,
        "time" => DateTime.Now.ToString(CultureInfo.InvariantCulture),
        _ => "Unknown command"
    };
    Console.WriteLine($"Got {command.Cmd} from {command.Context?.ConnectionId} responding with {response}");
    //Console.WriteLine($"Server got {command.Cmd} responding with {response}");
    server.Send(new CommandResponse() { Message = response }, client);
}

[ProtoContract]
public class TextMessage : TcpFlowHub
{
    [ProtoMember(1)]
    public string Message { get; init; }

    [ProtoMember(2)]
    public DateTime TimeSent { get; init; }
}

[ProtoContract]
public class Command : TcpFlowHub
{
    [ProtoMember(1)]
    public string Cmd { get; init; }

    [ProtoMember(2)]
    public string Arg { get; init; }
}

[ProtoContract]
public class CommandResponse : TcpFlowHub
{
    [ProtoMember(1)]
    public string Message { get; init; }
}

