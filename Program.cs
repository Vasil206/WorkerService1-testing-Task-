using App.Metrics.AspNetCore;
using App.Metrics.Formatters.Prometheus;
using Microsoft.AspNetCore.Hosting;
using WorkerService1;

IHostBuilder hostBuilder = Host.CreateDefaultBuilder(args)
    .UseMetricsWebTracking()
    .UseMetrics(options =>
    {
        options.EndpointOptions = endpointOptions =>
        {
            endpointOptions.MetricsEndpointOutputFormatter = new MetricsPrometheusTextOutputFormatter();
            endpointOptions.MetricsEndpointOutputFormatter = new MetricsPrometheusProtobufOutputFormatter();
            endpointOptions.EnvironmentInfoEndpointEnabled = false;
        };
    })
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<Worker>();
        services.Configure<Data>(context.Configuration.GetSection("Data"));
        services.AddMetrics();
    });


IHost host = hostBuilder.Build();
await host.RunAsync();

public class Data
{
    public int Interval { get; set; }
    public string[] ProcessNames { get; set; } = default!;
}