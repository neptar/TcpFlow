using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using MemoryPack;

namespace TcpFlow.Serializer;

/// <summary>
/// The default serializer. The TcpFlow source generator emits and registers a MemoryPack formatter for every flow
/// type and payload type, so the types themselves need no attributes.
/// </summary>
public sealed class MemoryPackFlowSerializer(MemoryPackSerializerOptions? options = null) : IFlowSerializer
{
    /// <summary>A shared instance using MemoryPack's default options.</summary>
    public static MemoryPackFlowSerializer Default { get; } = new();

    /// <inheritdoc />
    public void Serialize<T>(IBufferWriter<byte> writer, T message) where T : FlowObject
        => MemoryPackSerializer.Serialize(writer, message, options);

    /// <inheritdoc />
    public T Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlySpan<byte> data) where T : FlowObject
        => MemoryPackSerializer.Deserialize<T>(data, options)
           ?? throw new InvalidDataException($"MemoryPack deserialized a null {typeof(T).Name}.");
}
