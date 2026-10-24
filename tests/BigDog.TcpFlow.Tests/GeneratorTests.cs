using System.Collections.Immutable;
using MemoryPack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using BigDog.TcpFlow.Generator;

namespace BigDog.TcpFlow.Tests;

public class GeneratorTests
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Append(typeof(FlowObject).Assembly.Location)
        .Append(typeof(MemoryPackSerializer).Assembly.Location)
        .Distinct()
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    private sealed record Result(ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<Diagnostic> CompileErrors, string Source)
    {
        public IEnumerable<string> Ids => Diagnostics.Select(d => d.Id);

        public Diagnostic Single(string id) => Assert.Single(Diagnostics, d => d.Id == id);
    }

    private static Result Run(string code)
    {
        var compilation = CSharpCompilation.Create(
            "GeneratorTest",
            [CSharpSyntaxTree.ParseText("global using BigDog.TcpFlow; global using System; global using System.Collections.Generic;"), CSharpSyntaxTree.ParseText(code)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new FlowTypeGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var result = driver.GetRunResult();

        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        var source = result.GeneratedTrees.SingleOrDefault()?.ToString() ?? "";
        return new Result(result.Diagnostics, errors, source);
    }

    [Fact]
    public void Plain_data_types_generate_compiling_code_without_diagnostics()
    {
        var result = Run("""
            namespace App;

            public enum Kind { A, B }
            public struct Point { public int X; public int Y; }
            public class Payload { public string Name { get; set; } = ""; public List<Point> Points { get; set; } = []; }

            public abstract class Base : FlowObject { public Guid Id { get; set; } }

            public class Hello : Base
            {
                public const int Max = 10;
                public long Field;
                public required string Name { get; set; }
                public int? Age { get; init; }
                public Kind Kind { get; set; }
                public Payload? Payload { get; set; }
                public Dictionary<string, Kind[]> Lookup { get; set; } = [];
                public IReadOnlyList<Payload> All { get; set; } = [];

                public Hello() { Field = 1; }
                public override string ToString() => Name;
            }

            internal class InternalMessage : FlowObject { public int Value { get; set; } }

            public class Outer
            {
                public class Nested : FlowObject { public string Text { get; set; } = ""; }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.CompileErrors);
        Assert.Contains("Create<global::App.Hello>", result.Source);
        Assert.Contains("Create<global::App.InternalMessage>", result.Source);
        Assert.Contains("Create<global::App.Outer.Nested>(", result.Source);
        Assert.Contains("\"App.Outer.Nested\"", result.Source);
        Assert.DoesNotContain("Create<global::App.Base>", result.Source);

        // Payload types get formatters too, and collections used for them are registered for AOT.
        Assert.Contains("class App_PayloadFormatter", result.Source);
        Assert.Contains("class App_PointFormatter", result.Source);
        Assert.Contains("ListFormatter<global::App.Point>", result.Source);
        Assert.Contains("InterfaceReadOnlyListFormatter<global::App.Payload>", result.Source);
        Assert.Contains("ArrayFormatter<global::App.Kind>", result.Source);
        Assert.Contains("UnmanagedFormatter<global::App.Kind>", result.Source);

        // Base class members first; then properties in declaration order; then fields.
        var hello = result.Source[result.Source.IndexOf("class App_HelloFormatter", StringComparison.Ordinal)..];
        hello = hello[..hello.IndexOf("Deserialize", StringComparison.Ordinal)];
        string[] order = ["value.@Id", "value.@Name", "value.@Age", "value.@Kind", "value.@Payload", "value.@Lookup", "value.@All", "value.@Field"];
        Assert.Equal(order, order.OrderBy(m => hello.IndexOf(m, StringComparison.Ordinal)));
        Assert.Contains("WriteObjectHeader(8)", hello);
    }

    [Fact]
    public void Behaviour_on_a_flow_type_is_a_warning()
    {
        var result = Run("""
            namespace App;
            public class Busy : FlowObject
            {
                public int Value { get; set; }
                public Busy() { }
                public Busy(int value) { Value = value; }
                public void DoWork() { }
                public static Busy Create() => new();
                public static int Counter { get; set; }
                public static int SharedField;
                public event EventHandler? Changed;
                public int Doubled => Value * 2;
                public int this[int i] => i;
                public static Busy operator +(Busy a, Busy b) => a;
            }
            """);

        Assert.All(result.Diagnostics, d => Assert.Equal("TCPFLOW008", d.Id));
        Assert.All(result.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        var messages = result.Diagnostics.Select(d => d.GetMessage()).ToArray();
        Assert.Equal(9, messages.Length);
        Assert.Contains(messages, m => m.Contains("'Busy..ctor' is a constructor with parameters"));
        Assert.Contains(messages, m => m.Contains("'Busy.DoWork' is a method"));
        Assert.Contains(messages, m => m.Contains("'Busy.Changed' is a event"));
        Assert.Contains(messages, m => m.Contains("'Busy.Doubled' is a computed property"));
        Assert.Contains(messages, m => m.Contains("'Busy.Counter' is a static property"));
        Assert.Contains("Create<global::App.Busy>", result.Source);
        Assert.Empty(result.CompileErrors);
    }

    [Fact]
    public void State_that_is_not_transmitted_is_a_warning()
    {
        var result = Run("""
            namespace App;
            public class Leaky : FlowObject
            {
                private int _secret;
                public readonly int Fixed = 1;
                public string ReadOnly { get; } = "";
                public string PrivateSet { get; private set; } = "";
                internal int Internal { get; set; }
                protected int Protected { get; set; }
                public int Sent { get; set; }
            }
            """);

        Assert.Equal(6, result.Diagnostics.Count(d => d.Id == "TCPFLOW007"));
        Assert.Equal(6, result.Diagnostics.Length);
        Assert.Contains("WriteObjectHeader(1)", result.Source);
        Assert.Contains("value.@Sent", result.Source);
    }

    [Fact]
    public void Missing_parameterless_constructor_is_an_error()
    {
        var result = Run("""
            namespace App;
            public class NoDefault : FlowObject
            {
                public NoDefault(int value) { Value = value; }
                public int Value { get; set; }
            }
            public record Coordinates(int X, int Y);
            public class UsesRecord : FlowObject { public Coordinates? Where { get; set; } }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "TCPFLOW001" && d.GetMessage().Contains("'App.NoDefault'"));
        Assert.Contains(result.Diagnostics, d => d.Id == "TCPFLOW001" && d.GetMessage().Contains("'App.Coordinates'"));
        Assert.DoesNotContain("Create<global::App.NoDefault>", result.Source);
        Assert.DoesNotContain("Create<global::App.UsesRecord>", result.Source);
    }

    [Theory]
    [InlineData("object", "object can't be transmitted")]
    [InlineData("IDisposable", "interfaces other than the standard collection interfaces")]
    [InlineData("Action", "delegates can't be transmitted")]
    [InlineData("int[,]", "multi-dimensional arrays")]
    [InlineData("System.Text.StringBuilder", "framework type isn't supported")]
    [InlineData("AbstractPayload", "abstract types aren't supported")]
    [InlineData("FlowObject", "abstract types aren't supported")]
    [InlineData("List<object>", "object can't be transmitted")]
    [InlineData("System.Numerics.BigInteger", "transmit it as a string or byte[]")]
    public void Unsupported_member_type_is_an_error(string memberType, string reason)
    {
        var result = Run($$"""
            namespace App;
            public abstract class AbstractPayload { public int X { get; set; } }
            public class Holder : FlowObject { public {{memberType}}? Value { get; set; } }
            """);

        var diagnostic = result.Single("TCPFLOW006");
        Assert.Contains(reason, diagnostic.GetMessage());
        Assert.Contains("'Holder.Value'", diagnostic.GetMessage());
        Assert.DoesNotContain("Create<global::App.Holder>", result.Source);
    }

    [Fact]
    public void Problems_in_a_payload_type_are_reported_on_the_payload_and_block_the_message()
    {
        var result = Run("""
            namespace App;
            public class Payload { public object? Bad { get; set; } public void Helper() { } }
            public class Message : FlowObject { public Payload? Payload { get; set; } }
            """);

        Assert.Contains("'Payload.Bad'", result.Single("TCPFLOW006").GetMessage());
        Assert.Contains("'Payload.Helper'", result.Single("TCPFLOW008").GetMessage());
        Assert.DoesNotContain("Create<global::App.Message>", result.Source);
    }

    [Fact]
    public void MemoryPackable_on_a_flow_type_is_an_error_but_allowed_on_payloads()
    {
        var result = Run("""
            namespace App;
            [MemoryPack.MemoryPackable] public partial class Old : FlowObject { public int X { get; set; } }
            [MemoryPack.MemoryPackable] public partial class Custom { public int Y { get; set; } }
            public class UsesCustom : FlowObject { public Custom? Custom { get; set; } }
            """);

        Assert.Contains("'App.Old'", result.Single("TCPFLOW009").GetMessage());
        Assert.Single(result.Diagnostics);
        Assert.Contains("Create<global::App.UsesCustom>", result.Source);
        Assert.DoesNotContain("App_CustomFormatter", result.Source); // MemoryPack generates that one
    }

    [Fact]
    public void Members_split_across_partial_declarations_is_a_warning()
    {
        var result = Run("""
            namespace App;
            public partial class Split : FlowObject { public int A { get; set; } }
            public partial class Split { public int B { get; set; } }
            """);

        Assert.Equal("TCPFLOW010", Assert.Single(result.Diagnostics).Id);
        Assert.Single(result.Source.Split('\n'), line => line.Contains("Create<global::App.Split>"));
    }

    [Fact]
    public void Problems_in_a_shared_base_class_are_reported_once()
    {
        var result = Run("""
            namespace App;
            public abstract class Base : FlowObject { private int _hidden; public void Helper() { } }
            public class One : Base { }
            public class Two : Base { }
            """);

        Assert.Single(result.Diagnostics, d => d.Id == "TCPFLOW007");
        Assert.Single(result.Diagnostics, d => d.Id == "TCPFLOW008");
    }

    [Fact]
    public void Unresolved_member_types_are_left_to_the_compiler()
    {
        var result = Run("""
            namespace App;
            public class Typo : FlowObject { public Lsit<string>? Items { get; set; } }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain("Create<global::App.Typo>", result.Source);
    }

    [Fact]
    public void Private_nested_type_is_an_error()
    {
        var result = Run("""
            namespace App;
            public class Outer
            {
                private class Hidden : FlowObject { }
            }
            """);

        Assert.Equal("TCPFLOW002", Assert.Single(result.Diagnostics).Id);
        Assert.DoesNotContain("Hidden", result.Source);
    }

    [Fact]
    public void Generic_type_is_an_error()
    {
        var result = Run("""
            namespace App;
            public class Box<T> : FlowObject { public T? Value { get; set; } }
            """);

        Assert.Equal("TCPFLOW003", Assert.Single(result.Diagnostics).Id);
    }

    [Fact]
    public void Id_collision_is_an_error()
    {
        var result = Run("""
            namespace App;
            [FlowId(7)] public class A : FlowObject { }
            [FlowId(7)] public class B : FlowObject { }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TCPFLOW004", diagnostic.Id);
        Assert.Contains("App.B", diagnostic.GetMessage());
        Assert.Contains("Create<global::App.A>", result.Source);
        Assert.DoesNotContain("Create<global::App.B>", result.Source);
    }

    [Fact]
    public void FlowId_zero_is_an_error()
    {
        var result = Run("""
            namespace App;
            [FlowId(0)] public class A : FlowObject { }
            """);

        Assert.Equal("TCPFLOW005", Assert.Single(result.Diagnostics).Id);
    }

    [Fact]
    public void Nothing_is_generated_without_a_TcpFlow_reference()
    {
        var compilation = CSharpCompilation.Create("NoTcpFlow", [CSharpSyntaxTree.ParseText("class C : System.Object;")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var result = CSharpGeneratorDriver.Create(new FlowTypeGenerator()).RunGenerators(compilation).GetRunResult();
        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void Generator_output_is_cached_when_an_unrelated_file_changes()
    {
        var messages = CSharpSyntaxTree.ParseText("""
            namespace App;
            public class Payload { public string Name { get; set; } = ""; }
            public class Hello : BigDog.TcpFlow.FlowObject { public Payload? Payload { get; set; } }
            """);
        var other = CSharpSyntaxTree.ParseText("namespace App; class Unrelated { }");
        var compilation = CSharpCompilation.Create("Cache", [messages, other], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new FlowTypeGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        var edited = compilation.ReplaceSyntaxTree(other, CSharpSyntaxTree.ParseText("namespace App; class Unrelated { int x; }"));
        driver = driver.RunGenerators(edited);

        var outputs = driver.GetRunResult().Results.Single().TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs);
        Assert.All(outputs, o => Assert.True(o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
    }
}
