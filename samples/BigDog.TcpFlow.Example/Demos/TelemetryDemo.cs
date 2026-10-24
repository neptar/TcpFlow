using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;

namespace TcpFlow.Example.Demos;

/// <summary>
/// Throughput: several sensors stream readings concurrently over one connection each. Sends are safe from any
/// thread, and handlers for one connection run in order, so per-sensor ordering is preserved.
/// </summary>
public static class TelemetryDemo
{
    private const int Sensors = 4;
    private const int ReadingsPerSensor = 25_000;

    public static async Task RunAsync(ILoggerFactory loggerFactory)
    {
        var received = new ConcurrentDictionary<string, (int Count, double Sum)>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var total = 0;

        await using var server = new TcpFlowServer(new TcpFlowServerOptions
        {
            Port = 0,
            Address = IPAddress.Loopback,
            LoggerFactory = loggerFactory,
        });
        server.Map<SensorReading>((reading, _) =>
        {
            received.AddOrUpdate(reading.Sensor, (1, reading.Value), (_, s) => (s.Count + 1, s.Sum + reading.Value));
            if (Interlocked.Increment(ref total) == Sensors * ReadingsPerSensor)
                allReceived.TrySetResult();
        });
        server.Start();

        var clients = new List<TcpFlowClient>();
        for (var i = 0; i < Sensors; i++)
        {
            var client = new TcpFlowClient(new TcpFlowClientOptions
            {
                Host = "localhost",
                Port = server.LocalEndPoint!.Port,
                LoggerFactory = loggerFactory,
            });
            await client.ConnectAsync();
            clients.Add(client);
        }

        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(clients.Select((client, i) => Task.Run(async () =>
        {
            for (var n = 0; n < ReadingsPerSensor; n++)
            {
                await client.SendAsync(new SensorReading
                {
                    Sensor = $"sensor-{i}",
                    Value = 20 + i + Math.Sin(n / 100.0),
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }
        })));
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        foreach (var (sensor, (count, sum)) in received.OrderBy(r => r.Key))
            Console.WriteLine($"{sensor}: {count:N0} readings, mean {sum / count:F2}");
        Console.WriteLine($"{total:N0} messages in {stopwatch.ElapsedMilliseconds:N0} ms " +
                          $"({total / stopwatch.Elapsed.TotalSeconds:N0} msg/s)");

        foreach (var client in clients)
            await client.DisposeAsync();
    }
}
