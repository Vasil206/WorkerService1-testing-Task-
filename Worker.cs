using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace WorkerService1
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IOptionsMonitor<Data> _dataMonitor;
        private readonly Gauge _usageCpuGauge;
        private readonly Gauge _usageMemoryGauge;
        private const int ErrAccess = -1;
        private const int Err = -2;
        private static async Task<double> UsageCpuAsync(Process proc, int interval)
        {
            try
            {
                TimeSpan startUsageCpu = proc.TotalProcessorTime;
                long startTime = Environment.TickCount64;

                await Task.Delay(interval / 2);

                TimeSpan endUsageCpu = proc.TotalProcessorTime;

                double usedCpuMs = (endUsageCpu - startUsageCpu).TotalMilliseconds;
                double totalMsPassed = Environment.TickCount64 - startTime;
                double usageCpuTotal = usedCpuMs / totalMsPassed / Environment.ProcessorCount;

                return usageCpuTotal * 100;
            }
            catch (Win32Exception ex) when(ex.NativeErrorCode == 5)   //Access is denied
            {
                return ErrAccess;
            }
            catch   //Other
            {
                return Err;
            }

        }

        public Worker(ILogger<Worker> logger, IOptionsMonitor<Data> dataMonitor)
        {
            _logger = logger;
            _dataMonitor = dataMonitor;
            _usageCpuGauge = Metrics.CreateGauge(name: "processes_usage_cpu_percent",
                                                 help: "percentage of CPU using by interested processes",
                                                 labelNames: new[] { "name", "id" });
            _usageMemoryGauge = Metrics.CreateGauge(name: "processes_usage_rss_mb",
                                                    help: "resident set size of interested processes in MB",
                                                    labelNames: new[] { "name", "id" });
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                PeriodicTimer timer = new(TimeSpan.FromMilliseconds(10));
                int prevInterval = 10;
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    //checking on interval's changing
                    int interval = _dataMonitor.CurrentValue.Interval;
                    if (interval != prevInterval)
                    {
                        prevInterval = interval;
                        timer.Dispose();
                        timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
                    }

                    //going from names to Process
                    string[] processNames = _dataMonitor.CurrentValue.ProcessNames;
                    Process[][] processes = new Process[processNames.Length][];
                    for (int i = 0; i < processNames.Length; i++)
                    {
                        processes[i] = Process.GetProcessesByName(processNames[i]);
                    }

                    //making the array of async tasks with calculating of CPU usage
                    Task<double>[][] usageCpu = new Task<double>[processes.Length][];
                    for (int i = 0; i < usageCpu.Length; i++)
                    {
                        Array.Resize(ref usageCpu[i], processes[i].Length);

                        for (int j = 0; j < processes[i].Length; j++)
                            usageCpu[i][j] = UsageCpuAsync(processes[i][j], interval);  //starting of the calculating of CPU usage
                    }

                    //wait for the calculating of CPU usage
                    foreach (Task[] useCpu in usageCpu)
                        Task.WaitAll(useCpu);

                    //making the string for logging
                    for (int i = 0; i < processes.Length; i++)
                    {
                        for (int j = 0; j < processes[i].Length; j++)
                        {
                            string[] gaugeLabels = new[] { processes[i][j].ProcessName, Convert.ToString(processes[i][j].Id) };
                            _usageCpuGauge.Labels(gaugeLabels).Set(usageCpu[i][j].Result);
                            _usageMemoryGauge.Labels(gaugeLabels).Set(processes[i][j].WorkingSet64 / (1024 * 1024.0));
                        }
                    }

                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }
        }
    }
}