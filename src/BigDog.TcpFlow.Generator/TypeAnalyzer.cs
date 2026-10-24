using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TcpFlow.Generator;

/// <summary>
/// Works out how to serialize a flow type and every payload type it reaches, enforcing the "plain data" rules:
/// only public read/write properties and public non-readonly fields are transmitted.
/// </summary>
internal sealed class TypeAnalyzer
{
    private const int MaxMembers = 249; // MemoryPack object headers reserve 250-255.
    private const string FlowIdAttributeName = "TcpFlow.FlowIdAttribute";
    private const string MemoryPackableAttributeName = "MemoryPack.MemoryPackableAttribute";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>Unmanaged BCL types MemoryPack writes as raw bytes.</summary>
    private static readonly HashSet<string> UnmanagedBclTypes =
    [
        "System.DateTimeOffset", "System.TimeSpan", "System.Guid", "System.DateOnly", "System.TimeOnly",
        "System.Half", "System.Int128", "System.UInt128",
    ];

    /// <summary>Non-generic BCL types MemoryPack has built-in formatters for.</summary>
    private static readonly HashSet<string> WellKnownBclTypes =
    [
        "System.Uri", "System.Version", "System.TimeZoneInfo",
    ];

    /// <summary>Generic collections, keyed by metadata name, with the MemoryPack formatter that handles them.</summary>
    private static readonly Dictionary<string, string> CollectionFormatters = new()
    {
        ["System.Collections.Generic.List`1"] = "ListFormatter",
        ["System.Collections.Generic.Dictionary`2"] = "DictionaryFormatter",
        ["System.Collections.Generic.HashSet`1"] = "HashSetFormatter",
        ["System.Collections.Generic.SortedSet`1"] = "SortedSetFormatter",
        ["System.Collections.Generic.SortedDictionary`2"] = "SortedDictionaryFormatter",
        ["System.Collections.Generic.Queue`1"] = "QueueFormatter",
        ["System.Collections.Generic.Stack`1"] = "StackFormatter",
        ["System.Collections.Generic.LinkedList`1"] = "LinkedListFormatter",
        ["System.Collections.Generic.IEnumerable`1"] = "InterfaceEnumerableFormatter",
        ["System.Collections.Generic.ICollection`1"] = "InterfaceCollectionFormatter",
        ["System.Collections.Generic.IReadOnlyCollection`1"] = "InterfaceReadOnlyCollectionFormatter",
        ["System.Collections.Generic.IList`1"] = "InterfaceListFormatter",
        ["System.Collections.Generic.IReadOnlyList`1"] = "InterfaceReadOnlyListFormatter",
        ["System.Collections.Generic.ISet`1"] = "InterfaceSetFormatter",
        ["System.Collections.Generic.IReadOnlySet`1"] = "InterfaceReadOnlySetFormatter",
        ["System.Collections.Generic.IDictionary`2"] = "InterfaceDictionaryFormatter",
        ["System.Collections.Generic.IReadOnlyDictionary`2"] = "InterfaceReadOnlyDictionaryFormatter",
    };

    private static readonly HashSet<string> ObjectOverrides = ["ToString", "Equals", "GetHashCode"];

    private readonly Compilation _compilation;
    private readonly INamedTypeSymbol _flowObject;
    private readonly bool _reportDiagnostics;
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<ITypeSymbol> _visited = new(SymbolEqualityComparer.Default);
    private readonly List<ShapeModel> _shapes = [];
    private readonly Dictionary<string, RegistrationModel> _registrations = [];
    private readonly List<DiagnosticInfo> _diagnostics = [];
    private bool _hasErrors;

    private TypeAnalyzer(Compilation compilation, INamedTypeSymbol flowObject, bool reportDiagnostics, CancellationToken cancellationToken)
    {
        _compilation = compilation;
        _flowObject = flowObject;
        _reportDiagnostics = reportDiagnostics;
        _cancellationToken = cancellationToken;
    }

    /// <param name="fromSource">True for types in this compilation (diagnostics are reported); false for referenced ones.</param>
    public static FlowTypeModel AnalyzeFlowType(INamedTypeSymbol type, INamedTypeSymbol flowObject, Compilation compilation,
        bool fromSource, CancellationToken cancellationToken)
    {
        var analyzer = new TypeAnalyzer(compilation, flowObject, fromSource, cancellationToken);
        var qualifiedName = GetQualifiedName(type);
        var location = SourceLocation(type);

        var explicitId = type.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FlowIdAttributeName)
            ?.ConstructorArguments.FirstOrDefault().Value as uint?;
        if (explicitId == 0)
            analyzer.Report(Diagnostics.ReservedId, location, qualifiedName);

        analyzer.AnalyzeShape(type, isFlowType: true, fallbackLocation: location);

        return new FlowTypeModel(
            FullName: type.ToDisplayString(TypeFormat),
            QualifiedName: qualifiedName,
            Id: explicitId ?? ComputeId(qualifiedName),
            Location: location,
            IsValid: !analyzer._hasErrors,
            Shapes: analyzer._shapes.ToEquatableArray(),
            Registrations: analyzer._registrations.Values.OrderBy(r => r.TargetType, System.StringComparer.Ordinal).ToEquatableArray(),
            Diagnostics: analyzer._diagnostics.ToEquatableArray());
    }

    /// <summary>Validates a class or struct and records a formatter for it. Returns false if it can't be serialized.</summary>
    private bool AnalyzeShape(INamedTypeSymbol type, bool isFlowType, LocationInfo? fallbackLocation)
    {
        // Already analysed (or in progress, for recursive types): its result is already recorded.
        if (!_visited.Add(type))
            return true;

        _cancellationToken.ThrowIfCancellationRequested();
        var name = GetQualifiedName(type);
        var location = SourceLocation(type) ?? fallbackLocation;

        if (IsGeneric(type))
            return Report(Diagnostics.GenericType, location, name);
        if (!IsAccessible(type))
            return Report(Diagnostics.Inaccessible, location, name);

        var ok = true;
        if (isFlowType && HasAttribute(type, MemoryPackableAttributeName))
            ok = Report(Diagnostics.MemoryPackableFlowType, location, name);
        if (type.TypeKind == TypeKind.Class && !HasUsableParameterlessConstructor(type))
            ok = Report(Diagnostics.NoParameterlessConstructor, location, name);

        var members = new List<MemberModel>();
        foreach (var (member, memberType) in CollectMembers(type))
        {
            var memberLocation = SourceLocation(member) ?? fallbackLocation;
            var display = $"{type.Name}.{member.Name}";
            var kind = Classify(memberType, memberLocation, display, out var elementType);
            if (kind is null)
            {
                ok = false;
                continue;
            }
            members.Add(new MemberModel(member.Name, memberType.ToDisplayString(TypeFormat), kind.Value, elementType));
        }

        if (members.Count > MaxMembers)
            ok = Report(Diagnostics.TooManyMembers, location, name, members.Count.ToString());

        if (ok)
            _shapes.Add(new ShapeModel(type.ToDisplayString(TypeFormat), name, FormatterName(name), type.IsValueType, members.ToEquatableArray()));
        return ok;
    }

    /// <summary>
    /// Transmitted members, base class first. Within a class: properties in declaration order, then fields in declaration
    /// order. (Metadata doesn't preserve how fields and properties interleave, so this order is the same whether the type
    /// comes from source or from a referenced assembly.)
    /// </summary>
    private List<(ISymbol Member, ITypeSymbol Type)> CollectMembers(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var current = type; current is not null && !IsHierarchyRoot(current); current = current.BaseType)
            chain.Add(current);
        chain.Reverse();

        var result = new List<(ISymbol Member, ITypeSymbol Type)>();
        foreach (var level in chain)
        {
            var report = _reportDiagnostics && SymbolEqualityComparer.Default.Equals(level.ContainingAssembly, _compilation.Assembly);
            var properties = new List<(ISymbol, ITypeSymbol)>();
            var fields = new List<(ISymbol, ITypeSymbol)>();

            foreach (var member in level.GetMembers())
            {
                if (member.IsImplicitlyDeclared)
                    continue;

                var display = $"{level.Name}.{member.Name}";
                switch (member)
                {
                    case IFieldSymbol { IsConst: true }:
                    case IFieldSymbol { AssociatedSymbol: not null }:
                        break;
                    case IFieldSymbol { IsStatic: true } field:
                        if (report) ReportNotData(field, display, "static field");
                        break;
                    case IFieldSymbol field when field.DeclaredAccessibility == Accessibility.Public && !field.IsReadOnly:
                        fields.Add((field, field.Type));
                        break;
                    case IFieldSymbol field:
                        if (report) Report(Diagnostics.NotTransmitted, SourceLocation(field), display, "field");
                        break;

                    case IPropertySymbol { IsIndexer: true } property:
                        if (report) ReportNotData(property, display, "indexer");
                        break;
                    case IPropertySymbol { IsStatic: true } property:
                        if (report) ReportNotData(property, display, "static property");
                        break;
                    case IPropertySymbol property when IsPublicReadWrite(property):
                        properties.Add((property, property.Type));
                        break;
                    case IPropertySymbol property when property.SetMethod is not null || IsAutoProperty(property):
                        if (report) Report(Diagnostics.NotTransmitted, SourceLocation(property), display, "property");
                        break;
                    case IPropertySymbol property:
                        if (report) ReportNotData(property, display, "computed property");
                        break;

                    case IMethodSymbol method:
                        if (report && DescribeMethod(method) is { } kind)
                            ReportNotData(method, display, kind);
                        break;
                    case IEventSymbol @event:
                        if (report) ReportNotData(@event, display, "event");
                        break;
                }
            }

            if (report && SpansMultipleDeclarations(properties.Concat(fields).Select(m => m.Item1)))
                Report(Diagnostics.SplitMemberOrder, SourceLocation(level), GetQualifiedName(level));

            foreach (var member in properties.Concat(fields))
            {
                // A member hiding a base member with `new` replaces it.
                var hidden = result.FindIndex(m => m.Member.Name == member.Item1.Name);
                if (hidden >= 0)
                    result.RemoveAt(hidden);
                result.Add(member);
            }
        }

        return result;
    }

    /// <summary>Decides how a member type is written. Returns null (after reporting) if it can't be transmitted.</summary>
    private MemberKind? Classify(ITypeSymbol type, LocationInfo? location, string member, out string? elementType)
    {
        elementType = null;
        var display = type.ToDisplayString(TypeFormat);

        // The compiler already reports types that don't resolve; don't pile a misleading diagnostic on top.
        if (type.TypeKind == TypeKind.Error)
        {
            _hasErrors = true;
            return null;
        }

        if (type.SpecialType == SpecialType.System_String)
            return MemberKind.String;

        if (IsUnmanagedBuiltIn(type))
        {
            RegisterUnmanagedFormatters(type);
            return MemberKind.Unmanaged;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (array.Rank != 1)
                return Unsupported("multi-dimensional arrays aren't supported; use a jagged array or a list");

            elementType = array.ElementType.ToDisplayString(TypeFormat);
            Register(display, $"global::MemoryPack.Formatters.ArrayFormatter<{elementType}>");
            if (IsUnmanagedBuiltIn(array.ElementType))
            {
                RegisterUnmanagedFormatters(array.ElementType);
                return MemberKind.UnmanagedArray;
            }
            return Classify(array.ElementType, location, member, out _) is null ? null : MemberKind.Array;
        }

        if (type is not INamedTypeSymbol named)
            return Unsupported("only classes, structs, enums, arrays and collections are supported");

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            var underlying = named.TypeArguments[0];
            if (Classify(underlying, location, member, out _) is null)
                return null;
            Register(display, $"global::MemoryPack.Formatters.NullableFormatter<{underlying.ToDisplayString(TypeFormat)}>");
            return MemberKind.Value;
        }

        if (named.IsGenericType && CollectionFormatters.TryGetValue(GetMetadataName(named.OriginalDefinition), out var formatter))
        {
            foreach (var argument in named.TypeArguments)
            {
                if (Classify(argument, location, member, out _) is null)
                    return null;
            }
            var arguments = string.Join(", ", named.TypeArguments.Select(a => a.ToDisplayString(TypeFormat)));
            Register(display, $"global::MemoryPack.Formatters.{formatter}<{arguments}>");
            return MemberKind.Value;
        }

        if (WellKnownBclTypes.Contains(GetMetadataName(named)))
            return MemberKind.Value;

        // MemoryPack 1.21's BigInteger formatter writes zeros, so values would silently be lost.
        if (GetMetadataName(named) == "System.Numerics.BigInteger")
            return Unsupported("MemoryPack doesn't round-trip BigInteger correctly; transmit it as a string or byte[] instead");

        // Escape hatch for payloads: a [MemoryPackable] type brings its own (MemoryPack-generated) formatter.
        if (HasAttribute(named, MemoryPackableAttributeName))
            return MemberKind.Value;

        if (named.SpecialType == SpecialType.System_Object)
            return Unsupported("object can't be transmitted; use a concrete type");
        if (named.TypeKind == TypeKind.Interface)
            return Unsupported("interfaces other than the standard collection interfaces aren't supported; use a concrete type");
        if (named.TypeKind == TypeKind.Delegate)
            return Unsupported("delegates can't be transmitted");
        if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return Unsupported("only classes, structs, enums, arrays and collections are supported");
        if (named.IsAbstract || named.IsStatic)
            return Unsupported("abstract types aren't supported because the concrete type isn't transmitted");
        if (named.IsRefLikeType)
            return Unsupported("ref structs can't be transmitted");
        if (named.ContainingNamespace?.ToDisplayString() is { } ns && (ns == "System" || ns.StartsWith("System.")))
            return Unsupported("this framework type isn't supported");

        // A user payload type (or another flow type): it gets a generated formatter of its own.
        var isFlowType = InheritsFrom(named, _flowObject);
        var fromSource = SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, _compilation.Assembly);
        if (AnalyzeShape(named, isFlowType, location))
            return MemberKind.Value;

        // Problems in source types are reported on the type itself; referenced types need reporting here.
        if (!fromSource)
            Unsupported($"'{GetQualifiedName(named)}' isn't a plain data type (it needs a public parameterless constructor and must be public and non-generic)");
        return null;

        MemberKind? Unsupported(string reason)
        {
            Report(Diagnostics.UnsupportedMemberType, location, member, type.ToDisplayString(), reason);
            return null;
        }
    }

    private void RegisterUnmanagedFormatters(ITypeSymbol type)
    {
        // Enums and nullables need explicit formatters when used inside collections under AOT; MemoryPack does the same.
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var underlying = nullable.TypeArguments[0];
            Register(type.ToDisplayString(TypeFormat), $"global::MemoryPack.Formatters.NullableFormatter<{underlying.ToDisplayString(TypeFormat)}>");
            RegisterUnmanagedFormatters(underlying);
        }
        else if (type.TypeKind == TypeKind.Enum)
        {
            var display = type.ToDisplayString(TypeFormat);
            Register(display, $"global::MemoryPack.Formatters.UnmanagedFormatter<{display}>");
        }
    }

    private void Register(string targetType, string formatterType)
        => _registrations[targetType] = new RegistrationModel(targetType, formatterType);

    private static bool IsUnmanagedBuiltIn(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            return IsUnmanagedBuiltIn(nullable.TypeArguments[0]);
        if (type.TypeKind == TypeKind.Enum)
            return true;

        return type.SpecialType switch
        {
            SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_SByte or SpecialType.System_Byte
                or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double
                or SpecialType.System_Decimal or SpecialType.System_DateTime => true,
            _ => type is INamedTypeSymbol named && UnmanagedBclTypes.Contains(GetMetadataName(named)),
        };
    }

    private static bool IsPublicReadWrite(IPropertySymbol property)
        => property.DeclaredAccessibility == Accessibility.Public
           && property.GetMethod?.DeclaredAccessibility == Accessibility.Public
           && property.SetMethod?.DeclaredAccessibility == Accessibility.Public
           && property.RefKind == RefKind.None;

    private static bool IsAutoProperty(IPropertySymbol property)
        => property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is PropertyDeclarationSyntax
        {
            ExpressionBody: null,
            AccessorList: { } accessors,
        } && accessors.Accessors.All(a => a.Body is null && a.ExpressionBody is null);

    /// <summary>Null when the method is allowed (accessors, parameterless constructors, object overrides).</summary>
    private static string? DescribeMethod(IMethodSymbol method) => method.MethodKind switch
    {
        MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise => null,
        MethodKind.Constructor => method.Parameters.Length == 0 ? null : "constructor with parameters",
        MethodKind.StaticConstructor => "static constructor",
        MethodKind.Destructor => "finalizer",
        MethodKind.UserDefinedOperator or MethodKind.Conversion => "operator",
        MethodKind.Ordinary when method.IsOverride && ObjectOverrides.Contains(method.Name) => null,
        _ => "method",
    };

    private bool HasUsableParameterlessConstructor(INamedTypeSymbol type)
    {
        var sameAssembly = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _compilation.Assembly);
        return !type.IsAbstract && type.InstanceConstructors.Any(c => c.Parameters.Length == 0
            && (c.DeclaredAccessibility == Accessibility.Public
                || (sameAssembly && c.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal)));
    }

    private bool IsAccessible(INamedTypeSymbol type)
    {
        var sameAssembly = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _compilation.Assembly);
        for (var current = type; current is not null; current = current.ContainingType)
        {
            var ok = current.DeclaredAccessibility == Accessibility.Public
                     || (sameAssembly && current.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal);
            if (!ok)
                return false;
        }
        return true;
    }

    private bool IsHierarchyRoot(INamedTypeSymbol type)
        => SymbolEqualityComparer.Default.Equals(type, _flowObject)
           || type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;

    private static bool SpansMultipleDeclarations(IEnumerable<ISymbol> members)
        => members
            .SelectMany(m => m.DeclaringSyntaxReferences)
            .Select(r => r.GetSyntax().FirstAncestorOrSelf<TypeDeclarationSyntax>())
            .Where(d => d is not null)
            .Distinct()
            .Skip(1)
            .Any();

    private void ReportNotData(ISymbol member, string display, string kind)
        => Report(Diagnostics.NotData, SourceLocation(member), display, kind);

    /// <summary>Records a diagnostic. Always returns false so callers can write <c>return Report(...)</c> for errors.</summary>
    private bool Report(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments)
    {
        if (descriptor.DefaultSeverity == DiagnosticSeverity.Error)
            _hasErrors = true;
        if (_reportDiagnostics)
            _diagnostics.Add(new DiagnosticInfo(descriptor.Id, location, arguments.ToEquatableArray()));
        return false;
    }

    private static LocationInfo? SourceLocation(ISymbol symbol)
        => LocationInfo.From(symbol.Locations.FirstOrDefault(l => l.IsInSource));

    private static bool HasAttribute(INamedTypeSymbol type, string attributeName)
        => type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attributeName);

    internal static bool InheritsFrom(INamedTypeSymbol symbol, INamedTypeSymbol baseType)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        }
        return false;
    }

    private static bool IsGeneric(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType)
                return true;
        }
        return false;
    }

    /// <summary>"Namespace.Outer.Inner": stable across Roslyn versions, unlike display formats.</summary>
    internal static string GetQualifiedName(INamedTypeSymbol symbol)
    {
        var parts = new Stack<string>();
        for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is INamespaceSymbol { IsGlobalNamespace: true })
                break;
            parts.Push(current.MetadataName);
        }
        return string.Join(".", parts);
    }

    private static string GetMetadataName(INamedTypeSymbol symbol) => GetQualifiedName(symbol);

    /// <summary>A unique, valid identifier for the generated formatter class.</summary>
    private static string FormatterName(string qualifiedName)
        => qualifiedName.Replace("_", "__").Replace('.', '_').Replace('`', '_') + "Formatter";

    /// <summary>
    /// FNV-1a over the UTF-8 bytes of the type's qualified name. This is the wire id, so it must never change.
    /// </summary>
    internal static uint ComputeId(string qualifiedName)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(qualifiedName))
            {
                hash ^= b;
                hash *= 16777619u;
            }
            return hash == 0 ? 1u : hash;
        }
    }
}
