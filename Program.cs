using Microsoft.AspNetCore.Hosting;
using Prometheus;

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
            Metrics.SuppressDefaultMetrics();
            return Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                    {
                        webBuilder.UseUrls("http://localhost:1234");
                        webBuilder.UseStartup<Startup>();
                    }
                );
        }
    }

    public class Data
    {
        public  int Interval { get; set; }
        public string[] ProcessNames { get; set; } = default!;
    }
}