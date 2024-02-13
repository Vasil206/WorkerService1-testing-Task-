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
        public  int Interval { get; set; }
        public string[] ProcessNames { get; set; } = default!;
    }
}