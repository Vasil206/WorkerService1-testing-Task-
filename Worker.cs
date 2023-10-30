using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WorkerService1
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;
        private class ProcessCpuRss
        {
            private readonly string name;
            private readonly int id;
            private readonly double usageCpu;
            private readonly double usageRss;
            public ProcessCpuRss(string name, int id, double usageCpu, double usageRss)
            {
                this.name = name;
                this.id = id;
                this.usageCpu = usageCpu;
                this.usageRss = usageRss;
            }
            public override string ToString()
            {
                string res = "";
                res += "Name: " + name;
                res += ", id: " + id;

                if (Convert.ToInt32(usageCpu) == -1)
                    res += ", CPU % ERR_Acsess";
                else if (Convert.ToInt32(usageCpu) == -2)
                    res += ", CPU % ERR";
                else
                    res += ", CPU % " + usageCpu;

                res += ", RAM MB " + usageRss;
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

                await Task.Delay(interval / 5);

                stopWatch.Stop();
                TimeSpan endUsageCpu = proc.TotalProcessorTime;

                double usedCpuMs = (endUsageCpu - startUsageCpu).TotalMilliseconds;
                double totalMsPassed = stopWatch.ElapsedMilliseconds;
                double usageCpuTotal = usedCpuMs / totalMsPassed;

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
        
        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Stopwatch iterationTimeWorking = Stopwatch.StartNew();

                int interval = _configuration.GetSection("Date").GetSection("PollInterval").Get<int>();
                string[] processNames = _configuration.GetSection("date").GetSection("InterestingProcesses").Get<string[]>();

                Process[][]? processes = new Process[processNames.Length][];
                for (int i = 0; i < processNames.Length; i++)
                {
                    processes[i] = Process.GetProcessesByName(processNames[i]);
                }

                Task<double>[][]? usageCpu = new Task<double>[processes.Length][];
                for(int i = 0; i < usageCpu.Length; i++)
                {
                    if (processes[i] != null)
                    {
                        Array.Resize(ref usageCpu[i], processes[i].Length);

                        for (int j = 0; j < processes[i].Length; j++)
                            usageCpu[i][j] = UsageCpuAsync(processes[i][j], interval);
                    }
                }

                LinkedList<ProcessCpuRss> processesCpuRss = new LinkedList<ProcessCpuRss>();
                for(int i = 0; i < processes.Length; i++)
                {
                    if (processes[i] is not null)
                    {
                        for (int j = 0; j < processes[i].Length; j++)
                        {
                            processesCpuRss.AddLast(new ProcessCpuRss(processes[i][j].ProcessName,
                                                                    processes[i][j].Id,
                                                                    usageCpu[i][j].Result,
                                                                    processes[i][j].PagedMemorySize64 / (1024 * 1024.0)));
                        }
                    }
                }

                string logInform = "";
                foreach (ProcessCpuRss processCpuRss in processesCpuRss)
                    logInform += processCpuRss.ToString() + "\n\t";
              
                _logger.LogInformation(logInform);
                
                iterationTimeWorking.Stop();
                int delay = interval - Convert.ToInt32(iterationTimeWorking.ElapsedMilliseconds);
                delay = Math.Max(delay, 0);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }
}