using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace Generated;

[Generator]
public class TcpFlowHubIncrementalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Step 1: Find all class declarations
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => (ClassDeclarationSyntax)ctx.Node)
            .Collect();

        // Step 2: Filter to classes that inherit from TcpFlowHub
        var TcpFlowHubClasses = context.CompilationProvider.Combine(classDeclarations)
            .Select((tuple, _) =>
            {
                var (compilation, classDecls) = tuple;
                var TcpFlowHubType = compilation.GetTypeByMetadataName("TcpFlow.TcpFlowHub");
                if (TcpFlowHubType == null)
                    return ImmutableArray<INamedTypeSymbol>.Empty;

                var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
                foreach (var classDecl in classDecls)
                {
                    var model = compilation.GetSemanticModel(classDecl.SyntaxTree);
                    if (model.GetDeclaredSymbol(classDecl) is INamedTypeSymbol symbol &&
                        InheritsFrom(symbol, TcpFlowHubType))
                    {
                        result.Add(symbol);
                    }
                }
                return result.ToImmutable();
            });

        // Step 3: Generate the dispatcher code
        context.RegisterSourceOutput(TcpFlowHubClasses, (spc, classes) =>
        {

            if (classes.IsDefaultOrEmpty)
            {
                Debug.WriteLine("No TcpFlowHub classes found");
                return;
            }

            foreach (var c in classes)
            {
                Debug.WriteLine(c.ToDisplayString());
            }
            var source = GenerateTypeRegistry(classes);
            //var source = GenerateDispatcherClass(classes);
            spc.AddSource("TcpFlowHubDispatcher.g.cs", source);
            
            var partialSource = GeneratePartialHub(classes);
            spc.AddSource("TcpFlowHub.g.cs", partialSource);
        });
    }

     private static bool InheritsFrom(INamedTypeSymbol symbol, INamedTypeSymbol baseType)
     {
         var current = symbol.BaseType;
         while (current != null)
         {
             if (SymbolEqualityComparer.Default.Equals(current, baseType))
                 return true;
             current = current.BaseType;
         }
         return false;
     }
//
//     private static string GenerateDispatcherClass(ImmutableArray<INamedTypeSymbol> classes)
//     {
//         var classConverters = new StringBuilder("using TcpFlow;\r\nusing TcpFlow.ProtobufHelpers;");
//         var cases = new StringBuilder();
//         foreach (var type in classes)
//         {
//             cases.AppendLine($@"                
//             case ""{type.Name}"":
//                 Console.WriteLine(""Unwrapping {type.ToDisplayString()}"");
//                 var packet = Proto.Deserialize<{type.ToDisplayString()}>(data);
//                 Console.WriteLine($""Executing {{packet}}"");
//                 actionMap[actionName].Execute(packet);
//                 break;
// ");
//                 
//         }
//
//         classConverters.AppendLine(@$"
//  public static partial class TcpFlowHubDispatcher
// {{
//     public static void Dispatch(string actionName, Dictionary<string, IHubAction> actionMap, byte[] data)
//     {{
//         switch(actionName)
//         {{
//             {cases.ToString()}
//              default:
//                 Console.WriteLine($""Unknown actionName {{actionName}}"");
//                 return;
//         }}
//     }}
// }}
// ");
//         
//
//         return classConverters.ToString();
//     }
//     

    private static string GeneratePartialHub(ImmutableArray<INamedTypeSymbol> classes)
    {
        var source = new StringBuilder(@"
using ProtoBuf;

namespace TcpFlow
{
[ProtoContract]
");
        int index = 101;
        foreach (var type in classes)
        {
            source.AppendLine($"[ProtoInclude({index++}, typeof({type.Name}))]");
        }
        
        source.AppendLine(@"public partial class TcpFlowHub
{
}  
}");

        return source.ToString();
    
    }
    private static string GenerateTypeRegistry(ImmutableArray<INamedTypeSymbol> classes)
    {
        var source = new StringBuilder($@"
using System;
using System.Net;
using TcpFlow;
using TcpFlow.ProtobufHelpers;

namespace TcpFlow
{{
    public static class Builder
    {{
        public static TcpFlowServer CreateServer(int port)
        {{
            if (!TcpFlowTypeManager.Registered)
            {{
                Register();
            }}
            return new TcpFlowServer()
            {{
                Port = port,
                HostName = Dns.GetHostName(),
                Ip = IPAddress.Loopback
            }};
        }}
    
        public static TcpFlowClient CreateClient(string host, int port)
        {{
            if (!TcpFlowTypeManager.Registered)
            {{
                Register();
            }}
            return new TcpFlowClient()
            {{
                Port = port,
                HostName = host,
                Ip = Dns.GetHostAddresses(host)[0]
            }};
        }}
        private static void Register()
        {{
");
    
        foreach (var type in classes)
        {
            source.AppendLine($@"
            TcpFlowTypeManager.RegisterType<{type.Name}>(data => 
            {{
                return Proto.Deserialize<{type.Name}>(data);
            }});");
        }

        source.AppendLine(@"
        }
    }
}");

        return source.ToString();
    }
}