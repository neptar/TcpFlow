using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace BigDog.TcpFlow.Serializer;

/// <summary>
/// Turns flow objects into bytes and back. Both ends of a connection must use the same serializer.
/// </summary>
/// <remarks>
/// Generated code always calls these methods with a concrete message type, so implementations can rely on
/// compile-time generics (no reflection) and stay NativeAOT compatible. <c>T</c> on <see cref="Deserialize{T}"/> keeps
/// all members under trimming because serializers such as MemoryPack require it.
/// </remarks>
public interface IFlowSerializer
{
    /// <summary>Writes <paramref name="message"/> to <paramref name="writer"/>.</summary>
    void Serialize<T>(IBufferWriter<byte> writer, T message) where T : FlowObject;

    /// <summary>Reads a <typeparamref name="T"/> from <paramref name="data"/>. Must not keep a reference to <paramref name="data"/>.</summary>
    T Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlySpan<byte> data) where T : FlowObject;
}
