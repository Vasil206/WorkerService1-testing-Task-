using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace WorkerService1
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IOptionsMonitor<Data> _dataMonitor;
        private bool _dataChanged;

        private readonly Meter _meter;

        private async Task<KeyValuePair<NameId, CpuRssValue>> DoWorkAsync(Process proc, int interval)
        {
            double usageCpuTotal;
            NameId nameId = new NameId(proc.Id, proc.ProcessName);
            try
            {
                if (proc.HasExited) throw new Exception("process has exited");
                TimeSpan startUsageCpu = proc.TotalProcessorTime;
                long startTime = Environment.TickCount64;

                await Task.Delay(interval / 2);

                if (proc.HasExited) throw new Exception("process has exited");
                TimeSpan endUsageCpu = proc.TotalProcessorTime;
                long endTime = Environment.TickCount64;

                double usedCpuMs = (endUsageCpu - startUsageCpu).TotalMilliseconds;
                double totalMsPassed = endTime - startTime;
                usageCpuTotal = usedCpuMs / totalMsPassed / Environment.ProcessorCount * 100;
            }
            catch (Win32Exception ex)
            {
                usageCpuTotal = -ex.NativeErrorCode;
                _logger.LogWarning($"{nameId.Name} : {nameId.Id} => {ex.Message}");
            }
            catch when (proc.HasExited)
            {
                usageCpuTotal = 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                usageCpuTotal = -404;
            }

            if(!proc.HasExited)
                proc.Refresh();
            return new KeyValuePair<NameId, CpuRssValue>(nameId,
                                                        new CpuRssValue(usageCpuTotal, proc.WorkingSet64 / (1024 * 1024.0)));
        }

        public Worker(ILogger<Worker> logger, IOptionsMonitor<Data> dataMonitor)
        {
            _dataChanged = false;
            _logger = logger;

            _dataMonitor = dataMonitor;
            _dataMonitor.OnChange(_ => _dataChanged = true);
            _meter = new("cpu_rss_watcher");
        }

        private void ClearMeasurements(ref Dictionary<NameId, CpuRssValue> measurements)
        {
            var keys = measurements.Keys;
            foreach (var key in keys)
            {
                if (measurements[key].IsUsed)
                    measurements.Remove(key);
                else
                    measurements[key].IsUsed = true;
            }
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                Dictionary<NameId, CpuRssValue> measurements = new();
                _meter.CreateObservableGauge("worker_cpu_usage", () =>
                {
                    LinkedList<Measurement<double>> result = new();
                    foreach (var measurement in measurements)
                    {
                        result.AddLast(new Measurement<double>(measurement.Value.Cpu,
                            new KeyValuePair<string, object?>("process_name", measurement.Key.Name),
                            new KeyValuePair<string, object?>("process_id", measurement.Key.Id)));
                    }

                    return result;
                }, unit: "%");
                _meter.CreateObservableGauge("worker_rss_usage", () =>
                {
                    LinkedList<Measurement<double>> result = new();
                    foreach (var measurement in measurements)
                    {
                        result.AddLast(new Measurement<double>(measurement.Value.Rss,
                            new KeyValuePair<string, object?>("process_name", measurement.Key.Name),
                            new KeyValuePair<string, object?>("process_id", measurement.Key.Id)));
                    }

                    return result;
                }, unit: "MiB");

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

                    //making the array of async tasks with calculating of CPU usage;
                    var metricsLoop = new Task<KeyValuePair<NameId, CpuRssValue>>[processes.Length][];

                    for (int i = 0; i < metricsLoop.Length; i++)
                    {
                        Array.Resize(ref metricsLoop[i], processes[i].Length);
                        for (int j = 0; j < metricsLoop[i].Length; j++)
                        {
                            metricsLoop[i][j] =
                                DoWorkAsync(processes[i][j], interval); //starting of the calculating of CPU usage
                        }
                    }

                    ClearMeasurements(ref measurements);

                    //wait while works
                    foreach (Task[] useCpu in metricsLoop)
                        Task.WaitAll(useCpu);

                    //add to measurements
                    foreach (var results in metricsLoop)
                    foreach (var result in results)
                    {
                        bool isAdded = false;
                        foreach (var measurement in measurements)
                        {
                            if (SequenceEqual(measurement.Key, result.Result.Key))
                            {
                                measurements[measurement.Key] = result.Result.Value;
                                isAdded = true;
                                break;
                            }
                        }
                        if(!isAdded)
                            measurements.Add(result.Result.Key,result.Result.Value);
                    }

                    //on data change
                    if (_dataChanged)
                    {
                        _dataChanged = false;
                        processNames = _dataMonitor.CurrentValue.ProcessNames;
                        if (interval != _dataMonitor.CurrentValue.Interval)
                        {
                            interval = _dataMonitor.CurrentValue.Interval;
                            timer.Dispose();
                            timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }
            finally
            {
                _meter.Dispose();
            }
        }

        private bool SequenceEqual(NameId source, NameId target)
        {
            return source.Name == target.Name && source.Id == target.Id;
        }
    }
    class NameId
    {
        public readonly int Id;
        public readonly string Name;
        public NameId(int id, string name)
        {
            Id = id;
            Name = name;
        }
    }

    class CpuRssValue
    {
        public readonly double Cpu;
        public readonly double Rss;
        public bool IsUsed;
        public CpuRssValue(double cpu, double rss)
        {
            Cpu = cpu;
            Rss = rss;
            IsUsed = false;
        }
    }
}
