using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

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
            services.Configure<Data>(_config.GetSection("Data"));
            services.AddHostedService<Worker>();
            services.AddHostedService<Load>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }
    }
}
