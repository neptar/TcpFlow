using MemoryPack;
using static BigDog.TcpFlow.Tests.TestHelpers;

namespace BigDog.TcpFlow.Tests;

public class SerializationTests
{
    private static Order CreateOrder() => new()
    {
        CorrelationId = Guid.NewGuid(),
        Customer = "Ada",
        Quantity = 3,
        Discount = 10,
        Priority = Priority.High,
        Fallback = Priority.Low,
        PlacedAt = new DateTimeOffset(2026, 10, 24, 12, 30, 0, TimeSpan.FromHours(1)),
        Window = TimeSpan.FromMinutes(45),
        ShipTo = new Address { Street = "1 Loop Road", City = "Localhost" },
        Location = new GeoPoint { Lat = 51.5, Lon = -0.12 },
        Pickup = new GeoPoint { Lat = 1, Lon = 2 },
        History = [new Address { Street = "Old Street" }, new Address { Street = "Older Street", City = "Elsewhere" }],
        Counts = new() { ["a"] = 1, ["b"] = 2 },
        Flags = [Priority.Low, Priority.High],
        Notes = ["fragile", "gift"],
        Tags = ["x", "y"],
        Raw = [1, 2, 3, 255],
        Related = new Ping { Sequence = 9, Text = "nested flow object" },
        Link = new Uri("https://example.com/orders/1"),
        Revision = 42,
    };

    private static void AssertEqual(Order expected, Order actual)
    {
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
        Assert.Equal(expected.Customer, actual.Customer);
        Assert.Equal(expected.Quantity, actual.Quantity);
        Assert.Equal(expected.Discount, actual.Discount);
        Assert.Equal(expected.Priority, actual.Priority);
        Assert.Equal(expected.Fallback, actual.Fallback);
        Assert.Equal(expected.PlacedAt, actual.PlacedAt);
        Assert.Equal(expected.Window, actual.Window);
        Assert.Equal(expected.ShipTo!.Street, actual.ShipTo!.Street);
        Assert.Equal(expected.ShipTo.City, actual.ShipTo.City);
        Assert.Equal(expected.Location, actual.Location);
        Assert.Equal(expected.Pickup, actual.Pickup);
        Assert.Equal(expected.History.Select(a => (a.Street, a.City)), actual.History.Select(a => (a.Street, a.City)));
        Assert.Equal(expected.Counts, actual.Counts);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.Raw, actual.Raw);
        Assert.Equal(expected.Related!.Sequence, actual.Related!.Sequence);
        Assert.Equal(expected.Related.Text, actual.Related.Text);
        Assert.Equal(expected.Link, actual.Link);
        Assert.Equal(expected.Revision, actual.Revision);
    }

    [Fact]
    public void Plain_class_with_every_kind_of_member_round_trips()
    {
        var order = CreateOrder();
        var bytes = MemoryPackSerializer.Serialize(order);
        AssertEqual(order, MemoryPackSerializer.Deserialize<Order>(bytes)!);
    }

    [Fact]
    public void Nulls_and_defaults_round_trip()
    {
        var order = new Order { Customer = "", Notes = null!, Tags = null!, Raw = null!, History = null! };
        var copy = MemoryPackSerializer.Deserialize<Order>(MemoryPackSerializer.Serialize(order))!;

        Assert.Null(copy.Discount);
        Assert.Null(copy.Fallback);
        Assert.Null(copy.ShipTo);
        Assert.Null(copy.Pickup);
        Assert.Null(copy.Related);
        Assert.Null(copy.Link);
        Assert.Null(copy.Notes);
        Assert.Null(copy.Tags);
        Assert.Null(copy.Raw);
        Assert.Null(copy.History);
    }

    [Fact]
    public async Task Plain_class_round_trips_over_the_network()
    {
        var received = new TaskCompletionSource<Order>();
        await using var server = StartServer(s => s.Map<Order>((o, _) => received.TrySetResult(o)));
        await using var client = await ConnectAsync(server);
        var order = CreateOrder();

        await client.SendAsync(order);

        AssertEqual(order, await received.WithTimeout());
    }

    [Fact]
    public void Framework_types_round_trip()
    {
        var value = new FrameworkTypes
        {
            Date = new DateOnly(2026, 10, 24),
            Time = new TimeOnly(13, 37),
            Half = (Half)1.5,
            Big = Int128.MaxValue,
            Version = new Version(2, 0, 1),
            Zone = TimeZoneInfo.Utc,
            Dates = [new DateOnly(2000, 1, 1)],
            Ids = [Guid.NewGuid()],
            Timeouts = new() { [Priority.High] = TimeSpan.FromSeconds(1), [Priority.Low] = null },
        };

        var copy = MemoryPackSerializer.Deserialize<FrameworkTypes>(MemoryPackSerializer.Serialize(value))!;

        Assert.Equal(value.Date, copy.Date);
        Assert.Equal(value.Time, copy.Time);
        Assert.Equal(value.Half, copy.Half);
        Assert.Equal(value.Big, copy.Big);
        Assert.Equal(value.Version, copy.Version);
        Assert.Equal(value.Zone!.Id, copy.Zone!.Id);
        Assert.Equal(value.Dates, copy.Dates);
        Assert.Equal(value.Ids, copy.Ids);
        Assert.Equal(value.Timeouts, copy.Timeouts);
    }

    [Fact]
    public void Generated_formatter_writes_the_same_bytes_as_MemoryPack()
    {
        var message = new CompatMessage
        {
            Number = 7,
            MaybeNumber = 8,
            Priority = Priority.Normal,
            Text = "same bytes",
            When = DateTimeOffset.UnixEpoch,
            Items = ["a", "b"],
            Totals = new() { ["x"] = 10 },
            Bytes = [9, 8, 7],
            Words = ["hello", "world"],
            Address = new Address { Street = "Twin Street" },
        };
        var twin = new CompatTwin
        {
            Number = message.Number,
            MaybeNumber = message.MaybeNumber,
            Priority = message.Priority,
            Text = message.Text,
            When = message.When,
            Items = message.Items,
            Totals = message.Totals,
            Bytes = message.Bytes,
            Words = message.Words,
            Address = message.Address,
        };

        Assert.Equal(MemoryPackSerializer.Serialize(twin), MemoryPackSerializer.Serialize(message));
    }

    [Fact]
    public void Message_from_an_older_sender_with_fewer_members_leaves_the_rest_default()
    {
        var ping = MemoryPackSerializer.Deserialize<Ping>(MemoryPackSerializer.Serialize(new PingV0 { Sequence = 5 }))!;

        Assert.Equal(5, ping.Sequence);
        Assert.Null(ping.Text);
    }

    [Fact]
    public void Message_from_a_newer_sender_with_more_members_is_rejected()
    {
        var bytes = MemoryPackSerializer.Serialize(new PingV2 { Sequence = 5, Text = "hi", Extra = true });
        Assert.Throws<MemoryPackSerializationException>(() => MemoryPackSerializer.Deserialize<Ping>(bytes));
    }
}
