using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Prometheus;

namespace WorkerService1
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IOptionsMonitor<Data> _dataMonitor;
        private bool _dataChanged;

        private readonly Gauge _usageCpuGauge;
        private readonly Gauge _usageMemoryGauge;

        private static async Task<double> UsageCpuAsync(Process proc, int interval)
        {
            try
            {
                TimeSpan startUsageCpu = proc.TotalProcessorTime;
                long startTime = Environment.TickCount64;

                await Task.Delay(interval / 2);

                if (proc.HasExited) return 0;
                
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
            _usageCpuGauge = Metrics.CreateGauge(name: "worker_processes_usage_cpu_percent",
                                                 help: "percentage of CPU using by interested processes",
                                                 metricLabels);
            _usageMemoryGauge = Metrics.CreateGauge(name: "worker_processes_usage_rss_mb",
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
                    if (processNames[0] == "--all")
                        processes[0] = Process.GetProcesses();
                    else
                    {
                        for (int i = 0; i < processNames.Length; i++)
                        {
                            processes[i] = Process.GetProcessesByName(processNames[i]);
                        }
                    }


                    //making the array of async tasks with calculating of CPU usage; array of name and id
                    Task<double>[][] usageCpu = new Task<double>[processes.Length][];
                    string[][][] processesNameId = new string[processes.Length][][];

                    for (int i = 0; i < usageCpu.Length; i++)
                    {
                        Array.Resize(ref usageCpu[i], processes[i].Length);
                        Array.Resize(ref processesNameId[i], processes[i].Length);

                        for (int j = 0; j < processes[i].Length; j++)
                        {
                            usageCpu[i][j] =
                                UsageCpuAsync(processes[i][j], interval); //starting of the calculating of CPU usage

                            processesNameId[i][j] = new[]
                                { processes[i][j].ProcessName, Convert.ToString(processes[i][j].Id) };
                        }
                    }
                    
                    //wait for the calculating of CPU usage
                    foreach (Task[] useCpu in usageCpu)
                        Task.WaitAll(useCpu, stoppingToken);
                    
                    //making metrics
                    for (int i = 0; i < processesNameId.Length; i++)
                    {
                        for (int j = 0; j < processesNameId[i].Length; j++)
                        {
                            if (usageCpu[i][j].Result < 0)
                            {
                                string warning = processesNameId[i][j][0] + "  " +
                                                 processesNameId[i][j][1] + " : " +
                                                 new Win32Exception(-Convert.ToInt32(usageCpu[i][j].Result))
                                                     .Message;
                                _logger.LogWarning(warning);

                                _usageCpuGauge.Labels(processesNameId[i][j]).Remove();
                                _usageMemoryGauge.Labels(processesNameId[i][j]).Remove();

                            }
                            else
                            {
                                _usageCpuGauge.Labels(processesNameId[i][j]).Set(usageCpu[i][j].Result);
                                _usageMemoryGauge.Labels(processesNameId[i][j])
                                    .Set(processes[i][j].WorkingSet64 / (1024 * 1024.0));
                            }
                        }
                    }

                    //on data change
                    if (_dataChanged)
                    {
                        _dataChanged = false;
                        interval = _dataMonitor.CurrentValue.Interval;
                        processNames = _dataMonitor.CurrentValue.ProcessNames;
                        timer.Dispose();
                        timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));

                        foreach (string[][] process in processesNameId)
                            foreach (string[] proc in process)
                            {
                                _usageCpuGauge.Labels(proc).Remove();
                                _usageMemoryGauge.Labels(proc).Remove();
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