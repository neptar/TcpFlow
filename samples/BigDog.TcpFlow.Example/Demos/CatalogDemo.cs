using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace BigDog.TcpFlow.Example.Demos;

/// <summary>
/// Request/response with correlation ids: the client awaits a typed answer to each query, the server answers
/// from an async handler, and results carry nested MemoryPack objects.
/// </summary>
public static class CatalogDemo
{
    private static readonly Dictionary<string, Product> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["keyboard"] = new() { Name = "Mechanical keyboard", Price = 129.99m, Tags = ["input", "rgb"] },
        ["mouse"] = new() { Name = "Wireless mouse", Price = 49.50m, Tags = ["input"] },
        ["monitor"] = new() { Name = "27\" 4K monitor", Price = 399m, Tags = ["display"] },
    };

    public static async Task RunAsync(ILoggerFactory loggerFactory)
    {
        await using var server = new TcpFlowServer(new TcpFlowServerOptions
        {
            Port = 0,
            Address = IPAddress.Loopback,
            LoggerFactory = loggerFactory,
        });
        server.Map<PriceQuery>(async (query, ctx) =>
        {
            await Task.Delay(50, ctx.CancellationToken); // pretend to hit a database
            var result = Catalog.TryGetValue(query.ProductName, out var product)
                ? new PriceQueryResult { RequestId = query.RequestId, Product = product }
                : new PriceQueryResult { RequestId = query.RequestId, Error = $"No product called '{query.ProductName}'" };
            await ctx.ReplyAsync(result);
        });
        server.Start();

        await using var client = new TcpFlowClient(new TcpFlowClientOptions
        {
            Host = "localhost",
            Port = server.LocalEndPoint!.Port,
            LoggerFactory = loggerFactory,
        });
        var catalog = new CatalogClient(client);
        await client.ConnectAsync();

        // Queries are sent concurrently; each await gets the answer to its own question.
        var names = new[] { "keyboard", "mouse", "toaster", "monitor" };
        var results = await Task.WhenAll(names.Select(name => catalog.QueryAsync(name)));

        foreach (var result in results)
        {
            Console.WriteLine(result.Product is { } p
                ? $"{p.Name,-22} ${p.Price,7:F2}  [{string.Join(", ", p.Tags)}]"
                : $"error: {result.Error}");
        }
    }

    /// <summary>
    /// Turns fire-and-forget messages into awaitable calls by matching each result to its query's RequestId.
    /// </summary>
    private sealed class CatalogClient
    {
        private readonly TcpFlowClient _client;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PriceQueryResult>> _pending = new();

        public CatalogClient(TcpFlowClient client)
        {
            _client = client;
            _client.Map<PriceQueryResult>((result, _) =>
            {
                if (_pending.TryRemove(result.RequestId, out var waiter))
                    waiter.TrySetResult(result);
            });
            // Fail anything still waiting if the connection drops.
            _client.Disconnected += (_, _) =>
            {
                foreach (var id in _pending.Keys)
                    if (_pending.TryRemove(id, out var waiter))
                        waiter.TrySetException(new IOException("Disconnected before a result arrived."));
            };
        }

        public async Task<PriceQueryResult> QueryAsync(string productName, CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            var waiter = new TaskCompletionSource<PriceQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiter;
            try
            {
                await _client.SendAsync(new PriceQuery { RequestId = id, ProductName = productName }, cancellationToken);
                return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }
    }
}
