using Prometheus;
using WorkerService1;

Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions
{
    SuppressEventCounters = true,
    SuppressMeters = true,
    SuppressProcessMetrics = true
});

using var server = new KestrelMetricServer(port: 1234);
server.Start();

IHostBuilder hostBuilder = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<Worker>();
        services.Configure<Data>(context.Configuration.GetSection("Data"));
    });


IHost host = hostBuilder.Build();
host.Run();

public class Data
{
    public int Interval { get; set; }
    public string[] ProcessNames { get; set; } = default!;
}