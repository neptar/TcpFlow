using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TcpFlow.Generator;

/// <summary>
/// Finds every concrete <c>TcpFlow.FlowObject</c> subclass and generates <c>TcpFlow.FlowTypes</c>: a registry mapping
/// each type to a stable wire id, plus MemoryPack formatters for the flow types and the payload types they use, so
/// messages can be plain classes with no attributes.
/// </summary>
[Generator]
public sealed class FlowTypeGenerator : IIncrementalGenerator
{
    private const string FlowObjectMetadataName = "TcpFlow.FlowObject";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Flow types declared in this project. Only classes with a base list can derive from FlowObject, and each
        // match becomes an equatable model, so editing an unrelated file doesn't regenerate anything.
        var localTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (ctx, ct) => GetLocalModel(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

        // Public flow types from referenced assemblies (e.g. a shared messages library), so this project's registry
        // and formatters cover them even before anything in that assembly has been touched.
        var referenceInfo = context.CompilationProvider.Select(static (compilation, ct) => GetReferenceInfo(compilation, ct));

        context.RegisterSourceOutput(localTypes.Combine(referenceInfo),
            static (spc, input) => Execute(spc, input.Left, input.Right));
    }

    private static FlowTypeModel? GetLocalModel(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var declaration = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol symbol || symbol.IsAbstract)
            return null;

        // A partial class may have several declarations with a base list; analyse only the first so it's reported once.
        var first = symbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<ClassDeclarationSyntax>()
            .First(d => d.BaseList is not null);
        if (first != declaration)
            return null;

        var compilation = ctx.SemanticModel.Compilation;
        var flowObject = compilation.GetTypeByMetadataName(FlowObjectMetadataName);
        if (flowObject is null || !TypeAnalyzer.InheritsFrom(symbol, flowObject))
            return null;

        return TypeAnalyzer.AnalyzeFlowType(symbol, flowObject, compilation, fromSource: true, ct);
    }

    private static ReferenceInfo GetReferenceInfo(Compilation compilation, CancellationToken ct)
    {
        var flowObject = compilation.GetTypeByMetadataName(FlowObjectMetadataName);

        // Nothing to do without TcpFlow, and the TcpFlow library itself declares no flow types.
        if (flowObject is null || SymbolEqualityComparer.Default.Equals(flowObject.ContainingAssembly, compilation.Assembly))
            return ReferenceInfo.Disabled;

        var tcpFlowAssembly = flowObject.ContainingAssembly;
        var types = new List<FlowTypeModel>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            ct.ThrowIfCancellationRequested();
            if (SymbolEqualityComparer.Default.Equals(assembly, tcpFlowAssembly)
                || !assembly.Modules.Any(m => m.ReferencedAssemblies.Any(r => r.Name == tcpFlowAssembly.Name)))
                continue;

            foreach (var type in GetPublicFlowTypes(assembly.GlobalNamespace, flowObject, ct))
                types.Add(TypeAnalyzer.AnalyzeFlowType(type, flowObject, compilation, fromSource: false, ct));
        }

        return new ReferenceInfo(true, types.ToEquatableArray());
    }

    private static IEnumerable<INamedTypeSymbol> GetPublicFlowTypes(INamespaceOrTypeSymbol container, INamedTypeSymbol flowObject, CancellationToken ct)
    {
        foreach (var member in container.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (member is INamespaceSymbol ns)
            {
                foreach (var type in GetPublicFlowTypes(ns, flowObject, ct))
                    yield return type;
            }
            else if (member is INamedTypeSymbol { DeclaredAccessibility: Accessibility.Public } type)
            {
                if (type.TypeKind == TypeKind.Class && !type.IsAbstract && !type.IsGenericType && TypeAnalyzer.InheritsFrom(type, flowObject))
                    yield return type;
                foreach (var nested in GetPublicFlowTypes(type, flowObject, ct))
                    yield return nested;
            }
        }
    }

    private static void Execute(SourceProductionContext spc, ImmutableArray<FlowTypeModel> localTypes, ReferenceInfo references)
    {
        if (!references.Enabled)
            return;

        // The same payload or base-class problem can be found via several flow types; report each once.
        var reported = new HashSet<DiagnosticInfo>();
        foreach (var diagnostic in localTypes.SelectMany(t => t.Diagnostics))
        {
            if (reported.Add(diagnostic))
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        }

        // Types from referenced assemblies were validated by their own project's generator; invalid ones are skipped.
        var seen = new HashSet<string>();
        var candidates = localTypes.OrderBy(t => t.FullName, System.StringComparer.Ordinal)
            .Concat(references.Types)
            .Where(t => t.IsValid && seen.Add(t.FullName));

        var byId = new Dictionary<uint, FlowTypeModel>();
        var registered = new List<FlowTypeModel>();
        foreach (var model in candidates)
        {
            if (byId.TryGetValue(model.Id, out var existing))
            {
                var location = (model.Location ?? existing.Location)?.ToLocation();
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.IdCollision, location, $"0x{model.Id:X8}", model.QualifiedName, existing.QualifiedName));
                continue;
            }
            byId.Add(model.Id, model);
            registered.Add(model);
        }

        var shapes = registered.SelectMany(t => t.Shapes)
            .GroupBy(s => s.FullName).Select(g => g.First())
            .OrderBy(s => s.FullName, System.StringComparer.Ordinal)
            .ToList();
        var registrations = registered.SelectMany(t => t.Registrations)
            .GroupBy(r => r.TargetType).Select(g => g.First())
            .OrderBy(r => r.TargetType, System.StringComparer.Ordinal)
            .ToList();

        spc.AddSource("FlowTypes.g.cs", SourceEmitter.Render(registered, shapes, registrations));
    }
}
