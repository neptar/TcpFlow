namespace TcpFlow.Serializer;

public interface IFlowSerializer
{
    public T Deserialize<T>(byte[] bytes) where T : FlowObject;

    public byte[] Serialize<T>(T flowObject) where T : FlowObject;
}