namespace BigDog.TcpFlow.Example;

// Messages are plain classes deriving from FlowObject. The TcpFlow generator gives each one a wire id (a hash of its
// full name) and generates its serializer, so there's no reflection anywhere and the app can be published with AOT.
// Only public read/write properties and public fields are transmitted; the generator warns about anything else.

// --- Commands: simple request/response ---------------------------------------------------------------------------

public class Command : FlowObject
{
    public required string Cmd { get; set; }
    public string? Arg { get; set; }
}

public class CommandResponse : FlowObject
{
    public string Message { get; set; } = "";
}

// --- Chat: broadcasting and per-connection state -----------------------------------------------------------------

public class JoinChat : FlowObject
{
    public string UserName { get; set; } = "";
}

public class ChatMessage : FlowObject
{
    public string From { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
}

public class SystemNotice : FlowObject
{
    public string Text { get; set; } = "";
}

// --- Catalog: request correlation, async handlers and nested payloads --------------------------------------------

// Payload objects used by messages don't derive from FlowObject; they follow the same plain-data rules.
public class Product
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public List<string> Tags { get; set; } = [];
}

public class PriceQuery : FlowObject
{
    public Guid RequestId { get; set; }
    public string ProductName { get; set; } = "";
}

public class PriceQueryResult : FlowObject
{
    public Guid RequestId { get; set; }
    public Product? Product { get; set; }
    public string? Error { get; set; }
}

// --- Telemetry: explicit wire ids --------------------------------------------------------------------------------

// [FlowId] pins the wire id, so this type can be renamed or moved to another namespace without breaking older peers.
[FlowId(0x1000_0001)]
public class SensorReading : FlowObject
{
    public string Sensor { get; set; } = "";
    public double Value { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
