using System.Buffers.Binary;

namespace TcpFlow.Internal;

/// <summary>
/// Wire format of one frame, all little-endian:
/// <c>[int32 length][uint32 type id][payload]</c>, where length counts the type id and the payload.
/// </summary>
internal static class FrameHeader
{
    public const int Size = 8;
    private const int TypeIdSize = 4;

    public static void Write(Span<byte> destination, int payloadLength, uint typeId)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, payloadLength + TypeIdSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[TypeIdSize..], typeId);
    }

    /// <summary>Returns false when the length is invalid or the payload exceeds <paramref name="maxPayloadLength"/>.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, int maxPayloadLength, out int length, out uint typeId)
    {
        length = BinaryPrimitives.ReadInt32LittleEndian(source);
        typeId = BinaryPrimitives.ReadUInt32LittleEndian(source[TypeIdSize..]);
        return length >= TypeIdSize && length - TypeIdSize <= maxPayloadLength;
    }

    public static int PayloadLength(int length) => length - TypeIdSize;
}
