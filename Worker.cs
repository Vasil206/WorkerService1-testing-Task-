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
        private bool _dataChanged;
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
                long endTime = Environment.TickCount64;

                double usedCpuMs = (endUsageCpu - startUsageCpu).TotalMilliseconds;
                double totalMsPassed = endTime - startTime;
                double usageCpuTotal = usedCpuMs / totalMsPassed / Environment.ProcessorCount;

                return usageCpuTotal * 100;
            }
            catch (Win32Exception ex)
            {
                return -ex.NativeErrorCode;
            }
        }

        public Worker(ILogger<Worker> logger, IOptionsMonitor<Data> dataMonitor)
        {
            _dataChanged = false;
            _logger = logger;

            _dataMonitor = dataMonitor;
            _dataMonitor.OnChange(_ => _dataChanged = true);

            string[] metricLabels = new[] { "name", "id" };
            _usageCpuGauge = Metrics.CreateGauge(name: "processes_usage_cpu_percent",
                                                 help: "percentage of CPU using by interested processes",
                                                 metricLabels);
            _usageMemoryGauge = Metrics.CreateGauge(name: "processes_usage_rss_mb",
                                                    help: "resident set size of interested processes in MB",
                                                    metricLabels);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                string[] processNames = _dataMonitor.CurrentValue.ProcessNames;
                int interval = _dataMonitor.CurrentValue.Interval;

                PeriodicTimer timer = new(TimeSpan.FromMilliseconds(interval));
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    //going from names to Process
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
                        Task.WaitAll(useCpu,stoppingToken);

                    //making metrics
                    for (int i = 0; i < processes.Length; i++)
                    {
                        for (int j = 0; j < processes[i].Length; j++)
                        {
                            string[] gaugeLabels = new[] { processes[i][j].ProcessName, Convert.ToString(processes[i][j].Id) };

                            if (usageCpu[i][j].Result < 0)
                            {
                                string warning = processes[i][j].ProcessName + " ; " +
                                                 processes[i][j].Id + " : " +
                                                 new Win32Exception(-Convert.ToInt32(usageCpu[i][j].Result)).Message;
                                _logger.LogWarning(warning);
                            }

                            _usageCpuGauge.Labels(gaugeLabels).Set(usageCpu[i][j].Result);
                            _usageMemoryGauge.Labels(gaugeLabels).Set(processes[i][j].WorkingSet64 / (1024 * 1024.0));
                        }
                    }

                    //on data change
                    if (_dataChanged)
                    {
                        _dataChanged = false;
                        interval = _dataMonitor.CurrentValue.Interval;
                        processNames=_dataMonitor.CurrentValue.ProcessNames;
                        timer.Dispose();
                        timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
                        foreach (Process[] process in processes)
                            foreach (Process proc in process)
                            {
                                string[] gaugeLabels = new[] { proc.ProcessName, Convert.ToString(proc.Id) };
                                _usageCpuGauge.Labels(gaugeLabels).Remove();
                                _usageMemoryGauge.Labels(gaugeLabels).Remove();
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