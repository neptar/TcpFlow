using System.Net;
using System.Text;

namespace BigDog.TcpFlow.Tests;

public class RegistryTests
{
    private static uint Fnv1a(string name)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }

    [Fact]
    public void Generated_ids_are_the_fnv1a_hash_of_the_full_name()
    {
        Assert.True(FlowTypes.Registry.TryGet(typeof(Ping), out var info));
        Assert.Equal(Fnv1a("BigDog.TcpFlow.Tests.Ping"), info.Id);
        Assert.Equal("BigDog.TcpFlow.Tests.Ping", info.Name);
    }

    [Fact]
    public void FlowId_attribute_overrides_the_hash()
    {
        Assert.True(FlowTypes.Registry.TryGet(typeof(Pong), out var info));
        Assert.Equal(4242u, info.Id);
    }

    [Fact]
    public void Abstract_types_are_not_registered()
    {
        Assert.False(FlowTypes.Registry.Contains(typeof(AbstractMessage)));
    }

    [Fact]
    public void Module_initializer_fills_the_default_registry()
    {
        Assert.True(FlowTypeRegistry.Default.Contains(typeof(Ping)));
        Assert.True(FlowTypeRegistry.Default.Contains(typeof(Pong)));
    }

    [Fact]
    public void Conflicting_registrations_throw()
    {
        var registry = new FlowTypeRegistry();
        registry.Add(FlowTypeInfo.Create<Ping>(1, "Ping"));
        registry.Add(FlowTypeInfo.Create<Ping>(1, "Ping")); // identical: no-op

        Assert.Throws<InvalidOperationException>(() => registry.Add(FlowTypeInfo.Create<Pong>(1, "Pong")));
        Assert.Throws<InvalidOperationException>(() => registry.Add(FlowTypeInfo.Create<Ping>(2, "Ping")));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlowTypeInfo.Create<Ping>(0, "Ping"));
    }

    [Fact]
    public async Task Mapping_twice_throws()
    {
        await using var server = new TcpFlowServer(new TcpFlowServerOptions { Port = 0, Address = IPAddress.Loopback });
        server.Map<Ping>((_, _) => { });
        Assert.Throws<InvalidOperationException>(() => server.Map<Ping>((_, _) => { }));
    }

    [Fact]
    public async Task Mapping_an_unregistered_type_throws()
    {
        await using var server = new TcpFlowServer(new TcpFlowServerOptions { Port = 0, Registry = new FlowTypeRegistry() });
        Assert.Throws<InvalidOperationException>(() => server.Map<Ping>((_, _) => { }));
    }

    [Fact]
    public async Task Sending_an_unregistered_type_throws()
    {
        await using var server = new TcpFlowServer(new TcpFlowServerOptions { Port = 0, Address = IPAddress.Loopback, Registry = new FlowTypeRegistry() });
        server.Start();
        await using var client = new TcpFlowClient(new TcpFlowClientOptions
        {
            Host = "127.0.0.1",
            Port = server.LocalEndPoint!.Port,
            Registry = new FlowTypeRegistry(),
        });
        await client.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new Ping()).AsTask());
    }
}
