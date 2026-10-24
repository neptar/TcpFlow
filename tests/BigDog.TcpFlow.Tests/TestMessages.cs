using MemoryPack;

namespace BigDog.TcpFlow.Tests;

public class Ping : FlowObject
{
    public int Sequence { get; set; }
    public string Text { get; set; } = "";
}

[FlowId(4242)]
public class Pong : FlowObject
{
    public int Sequence { get; set; }
}

public class Blob : FlowObject
{
    public byte[] Data { get; set; } = [];
}

public abstract class AbstractMessage : FlowObject;

// --- A message using every supported kind of member -----------------------------------------------------------------

public enum Priority { Low, Normal, High }

public struct GeoPoint
{
    public double Lat;
    public double Lon;
}

public class Address
{
    public string Street { get; set; } = "";
    public string? City { get; set; }
}

public abstract class Envelope : FlowObject
{
    public Guid CorrelationId { get; set; }
}

public class Order : Envelope
{
    public required string Customer { get; set; }
    public int Quantity { get; set; }
    public int? Discount { get; set; }
    public Priority Priority { get; set; }
    public Priority? Fallback { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public TimeSpan Window { get; init; }
    public Address? ShipTo { get; set; }
    public GeoPoint Location { get; set; }
    public GeoPoint? Pickup { get; set; }
    public List<Address> History { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = [];
    public HashSet<Priority> Flags { get; set; } = [];
    public IReadOnlyList<string> Notes { get; set; } = [];
    public string[] Tags { get; set; } = [];
    public byte[] Raw { get; set; } = [];
    public Ping? Related { get; set; }
    public Uri? Link { get; set; }
    public long Revision;

    public const int MaxQuantity = 100; // constants are fine

    public override string ToString() => $"Order {CorrelationId} for {Customer}"; // object overrides are fine
}

// --- Wire compatibility with MemoryPack's own generator --------------------------------------------------------------

public class CompatMessage : FlowObject
{
    public int Number { get; set; }
    public int? MaybeNumber { get; set; }
    public Priority Priority { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset When { get; set; }
    public List<string> Items { get; set; } = [];
    public Dictionary<string, long> Totals { get; set; } = [];
    public byte[] Bytes { get; set; } = [];
    public string[] Words { get; set; } = [];
    public Address? Address { get; set; }
}

/// <summary>The same shape as <see cref="CompatMessage"/>, serialized by MemoryPack's generator instead of TcpFlow's.</summary>
[MemoryPackable]
public partial class CompatTwin
{
    public int Number { get; set; }
    public int? MaybeNumber { get; set; }
    public Priority Priority { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset When { get; set; }
    public List<string> Items { get; set; } = [];
    public Dictionary<string, long> Totals { get; set; } = [];
    public byte[] Bytes { get; set; } = [];
    public string[] Words { get; set; } = [];

    // Address is a plain TcpFlow payload; MemoryPack's analyzer needs telling that it has a formatter.
    [MemoryPackAllowSerialize]
    public Address? Address { get; set; }
}

/// <summary>An "older" Ping with fewer members, and a "newer" one with more.</summary>
[MemoryPackable]
public partial class PingV0
{
    public int Sequence { get; set; }
}

[MemoryPackable]
public partial class PingV2
{
    public int Sequence { get; set; }
    public string Text { get; set; } = "";
    public bool Extra { get; set; }
}

public class FrameworkTypes : FlowObject
{
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    public Half Half { get; set; }
    public Int128 Big { get; set; }
    public Version? Version { get; set; }
    public TimeZoneInfo? Zone { get; set; }
    public List<DateOnly> Dates { get; set; } = [];
    public Guid[] Ids { get; set; } = [];
    public Dictionary<Priority, TimeSpan?> Timeouts { get; set; } = [];
}
