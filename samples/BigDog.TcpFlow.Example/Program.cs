using Microsoft.Extensions.Logging;
using TcpFlow.Example.Demos;

// Usage: dotnet run -- [demo] [--verbose]
//   demo: commands | chat | catalog | telemetry | tls | interactive | all (default)
var verbose = args.Contains("--verbose") || args.Contains("-v");
var demo = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? "all";

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
    })
    .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning));

var demos = new Dictionary<string, Func<ILoggerFactory, Task>>
{
    ["commands"] = CommandsDemo.RunAsync,
    ["chat"] = ChatDemo.RunAsync,
    ["catalog"] = CatalogDemo.RunAsync,
    ["telemetry"] = TelemetryDemo.RunAsync,
    ["tls"] = TlsDemo.RunAsync,
};

if (demo == "interactive")
{
    await InteractiveDemo.RunAsync(loggerFactory);
    return 0;
}

if (demo == "all")
{
    foreach (var (name, run) in demos)
    {
        Console.WriteLine($"\n=== {name} ===");
        await run(loggerFactory);
    }
    return 0;
}

if (demos.TryGetValue(demo, out var selected))
{
    await selected(loggerFactory);
    return 0;
}

Console.Error.WriteLine($"Unknown demo '{demo}'. Choose one of: {string.Join(", ", demos.Keys)}, interactive, all.");
return 1;
