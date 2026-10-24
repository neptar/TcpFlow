using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace BigDog.TcpFlow.Generator;

// All models are made only of value-equal data (no symbols or syntax) so the incremental pipeline can cache them.

/// <summary>One flow type plus everything needed to serialize it.</summary>
internal sealed record FlowTypeModel(
    string FullName,
    string QualifiedName,
    uint Id,
    LocationInfo? Location,
    bool IsValid,
    EquatableArray<ShapeModel> Shapes,
    EquatableArray<RegistrationModel> Registrations,
    EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>A class or struct that gets a generated MemoryPack formatter: the flow type itself or a payload type it uses.</summary>
internal sealed record ShapeModel(
    string FullName,
    string QualifiedName,
    string FormatterName,
    bool IsValueType,
    EquatableArray<MemberModel> Members);

internal enum MemberKind
{
    String,
    Unmanaged,
    UnmanagedArray,
    Array,
    Value,
}

/// <summary>A transmitted property or field. <see cref="ElementTypeName"/> is set for arrays.</summary>
internal sealed record MemberModel(string Name, string TypeName, MemberKind Kind, string? ElementTypeName);

/// <summary>A MemoryPack formatter that must be registered for AOT, e.g. <c>ListFormatter&lt;string&gt;</c> for <c>List&lt;string&gt;</c>.</summary>
internal sealed record RegistrationModel(string TargetType, string FormatterType);

internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Arguments)
{
    public Diagnostic ToDiagnostic()
        => Diagnostic.Create(Diagnostics.ById[Id], Location?.ToLocation(), Arguments.Cast<object>().ToArray());
}

/// <summary>Flow types found in referenced assemblies, and whether this compilation should generate anything.</summary>
internal sealed record ReferenceInfo(bool Enabled, EquatableArray<FlowTypeModel> Types)
{
    public static readonly ReferenceInfo Disabled = new(false, EquatableArray<FlowTypeModel>.Empty);
}

internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);

    public static LocationInfo? From(Location? location)
        => location?.SourceTree is null ? null : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
}

/// <summary>An immutable array with value equality, for use in incremental generator models.</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T>
{
    public static readonly EquatableArray<T> Empty = new([]);

    private readonly T[]? _items;

    public EquatableArray(T[] items) => _items = items;

    public int Length => _items?.Length ?? 0;

    public bool Equals(EquatableArray<T> other) => (_items ?? []).SequenceEqual(other._items ?? []);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in _items ?? [])
            hash = unchecked(hash * 31 + item.GetHashCode());
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class EquatableArray
{
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> items) where T : IEquatable<T> => new(items.ToArray());
}
