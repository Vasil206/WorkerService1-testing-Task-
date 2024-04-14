using Microsoft.AspNetCore.Hosting;

namespace WorkerService1
{
    internal class Program
    {
        public static void Main(string[] args)
        {
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
        public  int Interval { get; init; }
        public string[] ProcessNames { get; init; } = default!;
    }

    public class WorkerOptions
    {
        public static readonly WorkerOptions Default = new();
        public string PrometheusConnection { get; set; } = "http://cpu_rss_wacher:1234/";
        public string NatsConnection { get; set; } = "nats://nats_server:4222/";
        public string StreamsAndSubjectsPrefix { get; set; } = "workerMetrics";
        public string MeterName { get; set; } = "cpu_rss_watcher";
    }
}