namespace BigDog.TcpFlow;

/// <summary>
/// Base class for every message that can flow over a TcpFlow connection.
/// </summary>
/// <remarks>
/// Subclasses are plain data classes: only public read/write properties and public fields are transmitted, and they
/// need a public parameterless constructor. The TcpFlow source generator finds every non-abstract subclass, registers it
/// in the generated <c>BigDog.TcpFlow.FlowTypes.Registry</c> (and in <see cref="FlowTypeRegistry.Default"/>), generates its
/// serializer, and reports anything that won't be transmitted.
/// </remarks>
public abstract class FlowObject;

/// <summary>
/// Gives a flow type an explicit wire id instead of the default (a hash of its full type name).
/// </summary>
/// <remarks>
/// Use this to keep a type compatible with older peers after renaming it or moving it to another namespace,
/// or to resolve an id collision reported by the generator. Id 0 is reserved.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FlowIdAttribute(uint id) : Attribute
{
    /// <summary>The wire id used for this type.</summary>
    public uint Id { get; } = id;
}
