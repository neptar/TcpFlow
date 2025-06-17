namespace TcpFlow.Serializer;

public class SerializerManager
{
    IFlowSerializer _serializer;

    public void UseJson()
    {
        _serializer = new JsonFlowSerializer();
    }
    
    public T Deserialize<T>(byte[] bytes) where T : FlowObject
    {
        return _serializer.Deserialize<T>(bytes);
    }
    
    public byte[] Serialize<T>(T record) where T : FlowObject
    {
        return _serializer.Serialize(record);
    }
}