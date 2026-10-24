using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace TcpFlow.Generator;

internal static class Diagnostics
{
    private const string Category = "TcpFlow";

    public static readonly DiagnosticDescriptor NoParameterlessConstructor = new(
        id: "TCPFLOW001",
        title: "Type needs a parameterless constructor",
        messageFormat: "'{0}' needs a public parameterless constructor so it can be recreated when received",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Inaccessible = new(
        id: "TCPFLOW002",
        title: "Type is not accessible",
        messageFormat: "'{0}' must be public or internal (including any containing types) so generated code can use it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericType = new(
        id: "TCPFLOW003",
        title: "Generic types are not supported",
        messageFormat: "'{0}' is generic (or nested in a generic type); flow types and their payloads must be non-generic",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor IdCollision = new(
        id: "TCPFLOW004",
        title: "Flow type id collision",
        messageFormat: "Flow type id {0} of '{1}' is already used by '{2}'; give one of them a different [FlowId]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReservedId = new(
        id: "TCPFLOW005",
        title: "Flow type id 0 is reserved",
        messageFormat: "Flow type '{0}' uses [FlowId(0)]; id 0 is reserved",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedMemberType = new(
        id: "TCPFLOW006",
        title: "Member type cannot be transmitted",
        messageFormat: "'{0}' has type '{1}', which cannot be transmitted: {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotTransmitted = new(
        id: "TCPFLOW007",
        title: "Member will not be transmitted",
        messageFormat: "'{0}' is not a public read/write {1}, so it will not be transmitted",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotData = new(
        id: "TCPFLOW008",
        title: "Flow types should contain only data",
        messageFormat: "'{0}' is a {1}; flow types and their payloads should be plain data with only public properties and fields",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MemoryPackableFlowType = new(
        id: "TCPFLOW009",
        title: "Flow types must not be [MemoryPackable]",
        messageFormat: "'{0}' must not be [MemoryPackable]: TcpFlow generates its serializer. Remove the attribute.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SplitMemberOrder = new(
        id: "TCPFLOW010",
        title: "Transmitted members are split across partial declarations",
        messageFormat: "'{0}' declares transmitted members in more than one partial declaration, so their wire order depends on file order; keep them in one declaration",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TooManyMembers = new(
        id: "TCPFLOW011",
        title: "Too many transmitted members",
        messageFormat: "'{0}' has {1} transmitted members; the maximum is 249",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly IReadOnlyDictionary<string, DiagnosticDescriptor> ById = new[]
    {
        NoParameterlessConstructor, Inaccessible, GenericType, IdCollision, ReservedId, UnsupportedMemberType,
        NotTransmitted, NotData, MemoryPackableFlowType, SplitMemberOrder, TooManyMembers,
    }.ToDictionary(d => d.Id);
}
