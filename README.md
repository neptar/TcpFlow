# BigDog.TcpFlow

[![NuGet](https://img.shields.io/nuget/v/BigDog.TcpFlow.svg)](https://www.nuget.org/packages/BigDog.TcpFlow)
[![CI](https://github.com/neptar/TcpFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/neptar/TcpFlow/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/neptar/TcpFlow/blob/main/LICENSE)

BigDog.TcpFlow lets .NET applications exchange strongly typed objects over TCP with very little setup. You declare plain message classes, map handlers, and send. A source generator discovers your message types at compile time and generates [MemoryPack](https://github.com/Cysharp/MemoryPack) serializers for them, so nothing relies on reflection and apps can be published with **Native AOT**.

```csharp
server.Map<Command>(async (command, ctx) =>
    await ctx.ReplyAsync(new CommandResponse { Message = $"You said {command.Arg}" }));

await client.SendAsync(new Command { Cmd = "echo", Arg = "hi" });
```

## Features

- **Plain classes as messages.** Derive from `FlowObject`; no attributes, no `partial`, no registration.
- **Compile-time checks.** The generator warns about anything that won't be transmitted, and errors on anything that can't be.
- **AOT and trimming safe.** A generated registry and generated MemoryPack formatters; the library builds with `IsAotCompatible`.
- **Fast, compact frames.** An 8-byte header, pooled buffers, `TCP_NODELAY` on by default. In local testing the telemetry example moves several hundred thousand small messages per second over loopback.
- **Thread-safe sending.** Send from any number of threads; frames never interleave.
- **Robust framing.** Partial reads, oversized or malformed frames, unknown types and disconnects mid-frame are all handled.
- **TLS** with standard validation by default, certificate pinning for self-signed certs, and optional mutual TLS.
- **Broadcasts** (serialized once for all recipients), per-connection replies, connection events.
- **Fully async**, with `CancellationToken` support, graceful shutdown and `IAsyncDisposable`.
- **Logging** through `Microsoft.Extensions.Logging`.

## Installation

```shell
dotnet add package BigDog.TcpFlow
```

The package brings in the TcpFlow source generator and the MemoryPack runtime. It supports **.NET 8, 9 and 10** (Native AOT included), and the generator works with the .NET 8 SDK and later.

## Quick start

### 1. Declare messages

Share these types between client and server (same project, or a shared class library).

```csharp
using BigDog.TcpFlow;

public class Command : FlowObject
{
    public required string Cmd { get; set; }
    public string? Arg { get; set; }
}

public class CommandResponse : FlowObject
{
    public string Message { get; set; } = "";
}
```

That's all: no attributes and no `partial`. See [Message rules](#message-rules) for what a message can contain.

### 2. Start a server

```csharp
await using var server = new TcpFlowServer(port: 5000);

server.Map<Command>(async (command, ctx) =>
{
    var reply = command.Cmd == "echo" ? command.Arg ?? "" : $"Unknown command {command.Cmd}";
    await ctx.ReplyAsync(new CommandResponse { Message = reply });
});

server.Start();
```

### 3. Connect a client

```csharp
await using var client = new TcpFlowClient("localhost", 5000);

client.Map<CommandResponse>((response, ctx) => Console.WriteLine(response.Message));

await client.ConnectAsync();
await client.SendAsync(new Command { Cmd = "echo", Arg = "hello" });
```

## Handlers

```csharp
endpoint.Map<T>(Action<T, TcpFlowContext> handler);
endpoint.Map<T>(Func<T, TcpFlowContext, Task> handler);
endpoint.Unmap<T>();
```

Servers and clients both use `Map`. Each message type can have one handler per endpoint; mapping a type twice, or mapping a type that isn't registered, throws `InvalidOperationException`.

`TcpFlowContext` gives a handler:

| Member | |
|---|---|
| `Connection` | The `TcpFlowConnection` the message arrived on (id, endpoints, TLS info, `SendAsync`, `Close`). |
| `ConnectionId` | Shortcut for `Connection.Id`. Unique per server, so it's a good key for per-client state. |
| `CancellationToken` | Cancelled when the connection closes or the endpoint stops. |
| `ReplyAsync(message)` | Sends a message back on the same connection. |

**Ordering:** messages from one connection are handled one at a time, in the order they were sent. An async handler holds up the next message from *that* connection until it completes; other connections are unaffected. If you don't need ordering, hand slow work off, for example to a `Channel<T>`.

**Errors:** an exception thrown by a handler is logged and the connection stays open. Messages whose type is unknown or unmapped, or which fail to deserialize, are logged and skipped.

## Sending

| Call | Sends to |
|---|---|
| `client.SendAsync(message)` | The server. |
| `ctx.ReplyAsync(message)` | The connection the current message came from. |
| `server.SendAsync(connectionId, message)` | One connection. Throws `KeyNotFoundException` if it isn't open. |
| `server.BroadcastAsync(message, filter?)` | Every open connection, or those matching `filter`. Returns how many were sent. |
| `connection.SendAsync(message)` | That connection. |

All of these are safe to call concurrently. `SendAsync` throws `InvalidOperationException` when the client isn't connected, the message type isn't registered, or the serialized message exceeds `MaxMessageSize`. It throws `IOException` if the network write fails, in which case the connection is closed. A broadcast logs failures for individual recipients and carries on with the rest.

```csharp
// Relay a chat message to everyone except its sender.
await server.BroadcastAsync(message, c => c.Id != ctx.ConnectionId);
```

## Connections and lifecycle

```csharp
server.ClientConnected    += (_, connection) => Console.WriteLine($"{connection} connected");
server.ClientDisconnected += (_, connection) => Console.WriteLine($"{connection} left");
client.Disconnected       += (_, connection) => Console.WriteLine("lost the server");

IReadOnlyCollection<TcpFlowConnection> open = server.Connections; // snapshot
server.TryGetConnection(id, out var connection);
```

- `server.Start()` binds the port and returns. Use port `0` to pick a free port, then read `server.LocalEndPoint`.
- `server.StopAsync()` / `DisposeAsync()` stops accepting, closes every connection and waits for running handlers to finish.
- `client.ConnectAsync()` returns once the client can send (TCP connected and, if enabled, the TLS handshake done). After a disconnect, call it again to reconnect.
- `client.DisconnectAsync()` / `DisposeAsync()` closes the connection.
- Stopping or disconnecting from *inside* a handler is fine; in that case the call doesn't wait for the handler (that would deadlock).

## TLS

Give the server a certificate with a private key, and set `UseTls` on the client:

```csharp
var server = new TcpFlowServer(new TcpFlowServerOptions
{
    Port = 5001,
    ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", password),
});

var client = new TcpFlowClient(new TcpFlowClientOptions
{
    Host = "example.com",
    Port = 5001,
    UseTls = true,
});
```

By default the client **fully validates** the server certificate (chain, host name and revocation). Several options change that:

| Client option | Effect |
|---|---|
| `PinnedServerCertificate` | Trust exactly this certificate (compared by SHA-256), ignoring chain and name errors. Ideal for self-signed certificates. |
| `ServerCertificateValidation` | Your own `RemoteCertificateValidationCallback`. |
| `TargetHost` | The name expected in the certificate, if it differs from `Host`. |
| `ClientCertificate` | A certificate to present for mutual TLS. |

On the server, `RequireClientCertificate = true` enables mutual TLS (validated against the system trust store unless you provide `ClientCertificateValidation`). Setting TLS options while `UseTls` is false is an error, so a misconfigured client can't silently fall back to plain TCP. `connection.IsEncrypted` and `connection.RemoteCertificate` show what was negotiated.

## Options

Options shared by `TcpFlowServerOptions` and `TcpFlowClientOptions`:

| Option | Default | |
|---|---|---|
| `MaxMessageSize` | 4 MiB | The largest payload sent or accepted. A larger incoming frame closes the connection. |
| `Serializer` | `MemoryPackFlowSerializer.Default` | Must match on both ends. |
| `Registry` | `FlowTypeRegistry.Default` | Known message types (see below). |
| `LoggerFactory` | none | Any `ILoggerFactory`. |
| `NoDelay` | `true` | Disables Nagle's algorithm so small messages go out immediately. |
| `TlsHandshakeTimeout` | 10 s | Slow or stalled handshakes are dropped. |
| `EnabledSslProtocols` | `None` (OS default) | |
| `CheckCertificateRevocation` | `true` | |

The server also takes `Port`, `Address` (default: all interfaces, IPv4 and IPv6), `ServerCertificate`, `RequireClientCertificate` and `ClientCertificateValidation`. The client also takes `Host`, `Port` and the TLS options above.

## Message rules

Messages, and the payload classes they contain, are **plain data**. The generator writes their serializers and checks them at compile time.

**A message type** must:

- derive from `FlowObject`, directly or through an abstract base class (whose members are transmitted too);
- be a non-abstract, non-generic class that is `public` or `internal`;
- have a parameterless constructor (implicit or declared).

**What's transmitted:** public properties with a public getter and a public `set` or `init` (including `required` ones), and public non-readonly fields. Nothing else is sent: not private fields, not get-only or private-setter properties, not computed properties.

**Member types** can be:

| | |
|---|---|
| Primitives | `bool`, `char`, all integer and floating-point types, `decimal`, `string` |
| Common structs | `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`, `Guid`, `Half`, `Int128`, `UInt128` |
| Other framework types | `Uri`, `Version`, `TimeZoneInfo` |
| Enums and nullables | any enum, and `T?` of any supported struct |
| Arrays | `T[]` |
| Collections | `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `SortedSet<T>`, `SortedDictionary<K,V>`, `Queue<T>`, `Stack<T>`, `LinkedList<T>`, and the interfaces `IEnumerable<T>`, `ICollection<T>`, `IReadOnlyCollection<T>`, `IList<T>`, `IReadOnlyList<T>`, `ISet<T>`, `IReadOnlySet<T>`, `IDictionary<K,V>`, `IReadOnlyDictionary<K,V>` |
| Payload types | your own classes and structs, following the same plain-data rules (no need to derive from `FlowObject`) |
| Other messages | another `FlowObject` type |
| MemoryPack types | a payload type marked `[MemoryPackable]` keeps its MemoryPack-generated serializer (useful when you need MemoryPack features such as version tolerance) |

Not supported: `object`, interfaces other than the collections above, abstract types (the concrete type isn't sent, so there's no polymorphism), delegates, multi-dimensional arrays, generic payload types, and other framework types. `BigInteger` is rejected because MemoryPack 1.21's formatter for it loses the value; send it as a `string` or `byte[]`.

```csharp
public class Product                      // a payload type: no FlowObject, same rules
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public List<string> Tags { get; set; } = [];
}

public class PriceQueryResult : FlowObject
{
    public Guid RequestId { get; set; }
    public Product? Product { get; set; }
}
```

**Keep them plain.** Methods, events, computed properties, constructors with parameters, statics and indexers aren't transmitted and usually mean behaviour has crept into a message; the generator warns about them (`TCPFLOW008`). Constants, a parameterless constructor and overrides of `ToString`, `Equals` and `GetHashCode` are allowed. To make the warnings fail the build, add this to `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.TCPFLOW007.severity = error
dotnet_diagnostic.TCPFLOW008.severity = error
```

**Wire order and versioning.** Members are written base class first; within each class, properties in declaration order, then fields in declaration order. A receiver accepts messages with *fewer* members than it knows (missing ones keep their defaults), so you can add members **at the end** and roll out receivers first. Removing, reordering or inserting members, or sending *more* members than the receiver knows, breaks compatibility. Keep a message's transmitted members in a single declaration; if they are split across `partial` files, their order depends on file order (`TCPFLOW010`).

## Message types and wire ids

The generator emits an internal `BigDog.TcpFlow.FlowTypes` class in every project that declares (or references) message types:

```csharp
FlowTypes.Registry          // a FlowTypeRegistry with this project's types
FlowTypeRegistry.Default    // filled automatically from every project's FlowTypes at startup
```

Each type travels with a 32-bit **wire id**. By default this is the FNV-1a hash of its full name (for example `MyApp.Messages.Command`), so both peers agree on the id without any coordination. Two consequences:

- **Renaming or moving a type changes its id.** To keep compatibility with peers that still use the old build, pin the id:

  ```csharp
  [FlowId(0x1000_0001)]
  public class SensorReading : FlowObject { ... }
  ```

- **Member changes follow the rules in [Message rules](#message-rules)**: append new members at the end.

Message types declared in a **shared class library** are included automatically, with their serializers, in every project that references it.

### Diagnostics

| Id | Severity | Meaning |
|---|---|---|
| `TCPFLOW001` | Error | The type has no public parameterless constructor (for example, a positional record). |
| `TCPFLOW002` | Error | The type (or a type containing it) is `private` or `protected`, so generated code can't reach it. |
| `TCPFLOW003` | Error | Generic message and payload types aren't supported. |
| `TCPFLOW004` | Error | Two types have the same wire id. Give one a different `[FlowId]`. |
| `TCPFLOW005` | Error | `[FlowId(0)]`: id 0 is reserved. |
| `TCPFLOW006` | Error | A transmitted member has a type that can't be sent (see the table above). The message explains why. |
| `TCPFLOW007` | Warning | Some state won't be transmitted: a private or internal field or property, a readonly field, or a property without a public setter. |
| `TCPFLOW008` | Warning | Behaviour on a message or payload: a method, event, computed property, constructor with parameters, static member, indexer or operator. |
| `TCPFLOW009` | Error | A message type is marked `[MemoryPackable]`. Remove it; TcpFlow generates the serializer. |
| `TCPFLOW010` | Warning | Transmitted members are split across `partial` declarations, so their wire order depends on file order. |
| `TCPFLOW011` | Error | More than 249 transmitted members (a MemoryPack limit). |

Errors in a payload type are reported on the payload, and stop every message that uses it from being registered.

## Custom serializers

MemoryPack is the default. To use something else, implement `IFlowSerializer` and set `Serializer` on both ends:

```csharp
public interface IFlowSerializer
{
    void Serialize<T>(IBufferWriter<byte> writer, T message) where T : FlowObject;
    T Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlySpan<byte> data) where T : FlowObject;
}
```

Generated code always calls these methods with the concrete message type, so a serializer that uses source generation (for example System.Text.Json with a `JsonSerializerContext`) stays AOT compatible. Such a serializer has to handle your types itself: the formatters TcpFlow generates are MemoryPack formatters. `MemoryPackFlowSerializer` also accepts `MemoryPackSerializerOptions`.

## Native AOT

```xml
<PublishAot>true</PublishAot>
```

```shell
dotnet publish -c Release
```

No extra configuration is needed: every formatter, including those for collections such as `List<Product>`, is generated and registered at startup. The example project publishes as a native executable and runs all its demos. During publish, MemoryPack.Core reports generic warnings about its own reflection fallbacks; those paths aren't used for generated formatters.

## Wire protocol

Each message is one frame. All integers are little-endian:

```
+----------------+----------------+-------------------------+
| length (int32) | type id (u32)  | payload (length-4 bytes)|
+----------------+----------------+-------------------------+
```

`length` counts the type id and the payload. A receiver closes the connection when `length < 4` or the payload exceeds `MaxMessageSize`. Because frames are length-delimited, a frame with an unknown type id is skipped without losing the connection.

## Examples

[`samples/BigDog.TcpFlow.Example`](https://github.com/neptar/TcpFlow/tree/main/samples/BigDog.TcpFlow.Example) contains runnable demos:

```shell
dotnet run --project samples/BigDog.TcpFlow.Example                 # run all demos
dotnet run --project samples/BigDog.TcpFlow.Example -- chat -v      # one demo, with debug logging
```

| Demo | Shows |
|---|---|
| `commands` | The basics: `Map`, `SendAsync`, `ReplyAsync`. |
| `chat` | Per-connection state, `BroadcastAsync` with a filter, connect/disconnect events. |
| `catalog` | Awaitable request/response using correlation ids, async handlers, nested payload objects. |
| `telemetry` | Many concurrent senders, ordering, throughput, `[FlowId]`. |
| `tls` | A self-signed certificate with pinning, and default validation rejecting it. |
| `interactive` | A command prompt. Run `interactive server` and `interactive client [host] [port]` in two terminals to talk across machines. |

## Building and testing

```shell
dotnet build
dotnet test        # runs against both the net8.0 and net10.0 builds (needs the .NET 8 runtime installed)
```

The test suite covers round-trips, concurrent senders, broadcasts, frames split across reads, malformed and oversized frames, disconnects mid-frame, TLS pinning and rejection, registry behaviour, serialization (including byte-for-byte compatibility with MemoryPack's own generator), and the generator's output, diagnostics and incremental caching. [`tests/PackageSmokeTest`](https://github.com/neptar/TcpFlow/tree/main/tests/PackageSmokeTest) builds a consumer against the packed `.nupkg`; CI runs it with the .NET 8 SDK. See [RELEASING.md](https://github.com/neptar/TcpFlow/blob/main/RELEASING.md) for how releases are published.

## Not included (yet)

- Automatic reconnect (call `ConnectAsync` again from the `Disconnected` event).
- Built-in request/response correlation (see the `catalog` demo for a small pattern).
- Heartbeats or keep-alive messages.

## License

[MIT](https://github.com/neptar/TcpFlow/blob/main/LICENSE)
