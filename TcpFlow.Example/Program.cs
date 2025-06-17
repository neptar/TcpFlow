// See https://aka.ms/new-console-template for more information

using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TcpFlow;
using TcpFlow.Example;


var c = new Command() { Cmd = "echo" };

Console.WriteLine(JsonSerializer.Serialize(c));


//var cert = new X509Certificate2("server.pfx", "password");

var server = Builder.CreateServer(54321);//.UseSsl(cert);
var clientOne = Builder.CreateClient(Dns.GetHostName(), 54321);//.UseSsl();
var clientTwo = Builder.CreateClient(Dns.GetHostName(), 54321);//.UseSsl();

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

void HandleCommand(Command command, TcpFlowContext context)
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
    //Console.WriteLine($"Got {command.Cmd} from {command.Context?.ConnectionId} responding with {response}");
    //Console.WriteLine($"Server got {command.Cmd} responding with {response}");
    server.Send(new CommandResponse() { Message = response }, context.ConnectionId);;
}



