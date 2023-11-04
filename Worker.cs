using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace WorkerService1
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IOptionsMonitor<Data> _dataMonitor;
        private class ProcessCpuRss
        {
            private readonly string _name;
            private readonly int _id;
            private readonly double _usageCpu;
            private readonly double _usageRss;
            public ProcessCpuRss(string name, int id, double usageCpu, double usageRss)
            {
                _name = name;
                _id = id;
                _usageCpu = usageCpu;
                _usageRss = usageRss;
            }
            public override string ToString()
            {
                string res = "";
                res += "Name: " + _name;
                res += ", id: " + _id;

                if (Convert.ToInt32(_usageCpu) == -1)
                    res += ", CPU % ERR_Access";
                else if (Convert.ToInt32(_usageCpu) == -2)
                    res += ", CPU % ERR";
                else
                    res += ", CPU % " + _usageCpu;

                res += ", RAM MB " + _usageRss;
                return res;
            }
        }
        private static async Task<double> UsageCpuAsync(Process proc, int interval)
        {
            try
            {
                TimeSpan startUsageCpu = proc.TotalProcessorTime;
                Stopwatch stopWatch = new Stopwatch();
                stopWatch.Start();

                await Task.Delay(interval / 2);

                stopWatch.Stop();
                TimeSpan endUsageCpu = proc.TotalProcessorTime;

                double usedCpuMs = (endUsageCpu - startUsageCpu).TotalMilliseconds;
                double totalMsPassed = stopWatch.ElapsedMilliseconds;
                double usageCpuTotal = usedCpuMs / totalMsPassed / Environment.ProcessorCount;

                return usageCpuTotal * 100;
            }
            catch(Exception ex)
            {
                if (ex.Message == "Access is denied.")
                    return -1;
                else
                    return -2;
            }

        }

        public Worker(ILogger<Worker> logger, IOptionsMonitor<Data> dataMonitor)
        {
            _logger = logger;
            _dataMonitor = dataMonitor;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_dataMonitor.CurrentValue.Interval));
            int prevInterval = _dataMonitor.CurrentValue.Interval;
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {

                int interval = _dataMonitor.CurrentValue.Interval;
                if (interval != prevInterval)
                {
                    prevInterval = interval;
                    timer.Dispose();
                    timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
                }

                string[] processNames = _dataMonitor.CurrentValue.ProcessNames;
                Process[][] processes = new Process[processNames.Length][];
                for (int i = 0; i < processNames.Length; i++)
                {
                    processes[i] = Process.GetProcessesByName(processNames[i]);
                }

                Task<double>[][] usageCpu = new Task<double>[processes.Length][];
                for(int i = 0; i < usageCpu.Length; i++)
                {
                    Array.Resize(ref usageCpu[i], processes[i].Length);

                    for (int j = 0; j < processes[i].Length; j++)
                        usageCpu[i][j] = UsageCpuAsync(processes[i][j], interval);
                }

                LinkedList<ProcessCpuRss> processesCpuRss = new();
                for(int i = 0; i < processes.Length; i++)
                {
                    for (int j = 0; j < processes[i].Length; j++)
                    {
                        processesCpuRss.AddLast(new ProcessCpuRss(processes[i][j].ProcessName,
                                                                processes[i][j].Id,
                                                                usageCpu[i][j].Result,
                                                                processes[i][j].WorkingSet64 / (1024 * 1024.0)));
                    }
                }

                string logInform = "";
                foreach (ProcessCpuRss processCpuRss in processesCpuRss)
                    logInform += processCpuRss.ToString() + "\n\t";
              
                _logger.LogInformation(logInform);
            }
        }
    }
}