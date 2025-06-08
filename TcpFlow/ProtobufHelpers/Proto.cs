using System.IO;
using ProtoBuf;

namespace TcpFlow.ProtobufHelpers;

public class Proto
{
    public static byte[] Serialize<T>(T record) where T : class
    {
        if (record == null) return null;
        using (var stream = new MemoryStream())
        {
            Serializer.Serialize(stream, record);
            return stream.ToArray();
        }
    }
    public static T Deserialize<T>(byte[] data) where T : class
    {
        if (data == null) return null;
        using (var stream = new MemoryStream(data))
        {
            return Serializer.Deserialize<T>(stream);
        }
    }
}