using WorkerService1;

IHostBuilder hostBuilder = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<Worker>();
        services.Configure<Data>(context.Configuration.GetSection("Data"));
    });


IHost host = hostBuilder.Build();
await host.RunAsync();

public class Data
{
    public int Interval { get; set; }
    public string[] ProcessNames { get; set; } = null!;
}