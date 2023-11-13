using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace WorkerService1
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IOptionsMonitor<Data> _dataMonitor;
        private const int ErrAccess = -1;
        private const int Err = -2;
        private static string ToLogFormatStr(string name, int id, double usageCpu, double usageRss)
        {
            string res = $"Name: {name}, Id: {id}, CPU %: ";

            if (Convert.ToInt32(usageCpu) == ErrAccess)
                res += "ERR_Access";
            else if (Convert.ToInt32(usageCpu) == Err)
                res += "ERR";
            else
                res += usageCpu;

            res += $", RSS MB: {usageRss}";
            return res ;
        }
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
                    string logInform = "";
                    for (int i = 0; i < processes.Length; i++)
                    {
                        for (int j = 0; j < processes[i].Length; j++)
                        {
                            logInform += ToLogFormatStr(processes[i][j].ProcessName,
                                processes[i][j].Id,
                                usageCpu[i][j].Result,  //getting CPU usage
                                processes[i][j].WorkingSet64 / (1024 * 1024.0));
                            logInform += "\n\t";
                        }
                    }

                    _logger.LogInformation(logInform);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }
        }
    }
}