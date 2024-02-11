using Microsoft.AspNetCore.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace WorkerService1
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            using MeterProvider meterProvider = Sdk.CreateMeterProviderBuilder()
                .AddMeter("cpu_rss_watcher")
                .AddPrometheusHttpListener(options => options.UriPrefixes = new[] { "http://localhost:1234/" }) 
                .Build();

            CreateHostBuilder(args).Build().Run();
        }

        private static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args).
                ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
        }
    }

    public class Data
    {
        public  int Interval { get; set; }
        public string[] ProcessNames { get; set; } = default!;
    }
}