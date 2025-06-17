namespace TcpFlow;

//Source generator will search consumers and register actions here
public static class TcpFlowTypeManager
{
    private static readonly Dictionary<string, Func<byte[], FlowObject>> _typeRegistry = new();
    public static bool Registered => _typeRegistry.Count > 0;
    public static void RegisterType<T>(Func<byte[], T> deserializer) where T : FlowObject
    {
        _typeRegistry[typeof(T).Name] = deserializer;
    }

    public static FlowObject Deserialize(string typeName, byte[] data)
    {
        if (_typeRegistry.TryGetValue(typeName, out var deserializer))
        {
            return deserializer(data);
        }
        throw new ArgumentException($"Unknown type: {typeName}");
    }
}