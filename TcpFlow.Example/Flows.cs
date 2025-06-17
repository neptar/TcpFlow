namespace TcpFlow.Example;

public class TextMessage : FlowObject
{
    public string Message { get; set; }
    
    public DateTime TimeSent { get; set; }
}

public class Command : FlowObject
{
    public required string Cmd { get; set; }

    public string? Arg { get; set; }
}

public class CommandResponse : FlowObject
{
    public string Message { get; set; }
}

public class Product : FlowObject
{
    public string Name { get; set; }
    public int Price { get; set; }
}