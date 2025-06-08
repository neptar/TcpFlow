namespace TcpFlow;

//Source generator will search consumers and register actions here
public static class TcpFlowTypeManager
{
    private static readonly Dictionary<string, Func<byte[], TcpFlowHub>> _typeRegistry = new();
    public static bool Registered => _typeRegistry.Count > 0;
    public static void RegisterType<T>(Func<byte[], T> deserializer) where T : TcpFlowHub
    {
        _typeRegistry[typeof(T).Name] = deserializer;
    }

    public static TcpFlowHub Deserialize(string typeName, byte[] data)
    {
        if (_typeRegistry.TryGetValue(typeName, out var deserializer))
        {
            return deserializer(data);
        }
        throw new ArgumentException($"Unknown type: {typeName}");
    }
}