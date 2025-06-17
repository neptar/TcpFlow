using System.Text;
using System.Text.Json;

namespace TcpFlow.Serializer;

public class JsonFlowSerializer:IFlowSerializer
{
    public T Deserialize<T>(byte[] bytes) where T : FlowObject
    {
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException();
    }

    public byte[] Serialize<T>(T flowObject) where T : FlowObject
    {
        var jsonString = JsonSerializer.Serialize(flowObject);
        return Encoding.UTF8.GetBytes(jsonString);
    }
}