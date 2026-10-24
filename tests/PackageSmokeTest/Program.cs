using System.Net;
using BigDog.TcpFlow;

// A consumer-eye check of the published package: plain classes, generated serializers, one round trip.
if (FlowTypes.Registry.Types.Count != 1)
{
    Console.Error.WriteLine("FAIL: the source generator did not run.");
    return 1;
}

await using var server = new TcpFlowServer(new TcpFlowServerOptions { Port = 0, Address = IPAddress.Loopback });
var received = new TaskCompletionSource<Order>();
server.Map<Order>((order, _) => received.TrySetResult(order));
server.Start();

await using var client = new TcpFlowClient("127.0.0.1", server.LocalEndPoint!.Port);
await client.ConnectAsync();
await client.SendAsync(new Order { Customer = "Ada", Lines = [new() { Sku = "KB-1", Quantity = 2 }] });

var order = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
var ok = order.Customer == "Ada" && order.Lines is [{ Sku: "KB-1", Quantity: 2 }];
Console.WriteLine(ok
    ? $"PASS: BigDog.TcpFlow on .NET {Environment.Version}"
    : "FAIL: the message did not round-trip.");
return ok ? 0 : 1;

public class Order : FlowObject
{
    public string Customer { get; set; } = "";
    public List<OrderLine> Lines { get; set; } = [];
}

public class OrderLine
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
}
