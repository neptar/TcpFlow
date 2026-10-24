using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using BigDog.TcpFlow.Serializer;

namespace BigDog.TcpFlow;

/// <summary>Writes a flow object to <paramref name="writer"/> using <paramref name="serializer"/>.</summary>
public delegate void FlowSerializeHandler(IFlowSerializer serializer, IBufferWriter<byte> writer, FlowObject message);

/// <summary>Reads a flow object from <paramref name="data"/> using <paramref name="serializer"/>.</summary>
public delegate FlowObject FlowDeserializeHandler(IFlowSerializer serializer, ReadOnlySpan<byte> data);

/// <summary>Describes one registered flow type: its wire id and how to (de)serialize it.</summary>
public sealed class FlowTypeInfo
{
    private FlowTypeInfo(uint id, Type type, string name, FlowSerializeHandler serialize, FlowDeserializeHandler deserialize)
    {
        Id = id;
        Type = type;
        Name = name;
        Serialize = serialize;
        Deserialize = deserialize;
    }

    /// <summary>The id written in the frame header.</summary>
    public uint Id { get; }

    /// <summary>The CLR type.</summary>
    public Type Type { get; }

    /// <summary>The full type name, used for diagnostics.</summary>
    public string Name { get; }

    /// <summary>Serializes an instance of <see cref="Type"/>.</summary>
    public FlowSerializeHandler Serialize { get; }

    /// <summary>Deserializes an instance of <see cref="Type"/>.</summary>
    public FlowDeserializeHandler Deserialize { get; }

    /// <summary>
    /// Creates the type info for <typeparamref name="T"/>. Called by generated code; because <typeparamref name="T"/>
    /// is known at compile time the serializer calls need no reflection, which keeps them NativeAOT safe.
    /// </summary>
    public static FlowTypeInfo Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(uint id, string name) where T : FlowObject
    {
        if (id == 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Flow type id 0 is reserved.");

        return new FlowTypeInfo(id, typeof(T), name,
            static (serializer, writer, message) => serializer.Serialize(writer, (T)message),
            static (serializer, data) => serializer.Deserialize<T>(data));
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} (0x{Id:X8})";
}

/// <summary>
/// Maps flow types to wire ids. The source generator emits one registry per project
/// (<c>BigDog.TcpFlow.FlowTypes.Registry</c>) and merges it into <see cref="Default"/> at module load.
/// </summary>
public sealed class FlowTypeRegistry
{
    private readonly ConcurrentDictionary<uint, FlowTypeInfo> _byId = new();
    private readonly ConcurrentDictionary<Type, FlowTypeInfo> _byType = new();
    private readonly object _gate = new(); // System.Threading.Lock needs .NET 9+

    /// <summary>
    /// The process-wide registry that servers and clients use unless
    /// <see cref="TcpFlowOptions.Registry"/> is set.
    /// </summary>
    public static FlowTypeRegistry Default { get; } = new();

    /// <summary>A snapshot of the registered types.</summary>
    public IReadOnlyCollection<FlowTypeInfo> Types => _byId.Values.ToArray();

    /// <summary>Registers a type. Registering the same type with the same id again is a no-op.</summary>
    /// <exception cref="InvalidOperationException">The id or the type is already registered differently.</exception>
    public void Add(FlowTypeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (_gate)
        {
            if (_byId.TryGetValue(info.Id, out var existing))
            {
                if (existing.Type == info.Type)
                    return;
                throw new InvalidOperationException(
                    $"Flow type id 0x{info.Id:X8} is used by both '{existing.Name}' and '{info.Name}'. Give one of them a [FlowId].");
            }

            if (_byType.TryGetValue(info.Type, out existing))
                throw new InvalidOperationException(
                    $"'{info.Name}' is already registered with id 0x{existing.Id:X8} and cannot be registered again as 0x{info.Id:X8}.");

            _byType[info.Type] = info;
            _byId[info.Id] = info;
        }
    }

    /// <summary>Registers every type from <paramref name="other"/>.</summary>
    public void Add(FlowTypeRegistry other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var info in other.Types)
            Add(info);
    }

    /// <summary>Looks a type up by wire id.</summary>
    public bool TryGet(uint id, [MaybeNullWhen(false)] out FlowTypeInfo info) => _byId.TryGetValue(id, out info);

    /// <summary>Looks a type up by CLR type.</summary>
    public bool TryGet(Type type, [MaybeNullWhen(false)] out FlowTypeInfo info) => _byType.TryGetValue(type, out info);

    /// <summary>Whether <paramref name="type"/> is registered.</summary>
    public bool Contains(Type type) => _byType.ContainsKey(type);
}
