using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using OpenTelemetry.Metrics;

namespace WorkerService1
{
    internal class Startup
    {
        private readonly IConfiguration _config;
        public Startup(IConfiguration config)
        {
            _config = config;
        }
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddOpenTelemetry()
                .WithMetrics(b =>
                {
                    string? outerHostName = _config.GetSection("OuterHostName").Value;
                    var path = $"http://{outerHostName}:1234/";

                    b.AddMeter("cpu_rss_watcher");
                    b.AddPrometheusHttpListener(opt => opt.UriPrefixes = new []{path});
                });
            services.Configure<Data>(_config.GetSection("Data"));
            services.AddHostedService<Worker>();
            services.AddHostedService<Load>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {

        }
    }
}
