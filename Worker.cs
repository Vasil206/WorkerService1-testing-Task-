using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace WorkerService1
{
    public class Worker : BackgroundService//////////////////////////SubjectName, StreamName,"nats://nats_server:4222", e.g. -- getting from config by IOption<T>
    {
        private const string SubjectName = "worker_metrics";
        private const string StreamName = "worker_metrics";

        private readonly NatsConnection _nats;
        private readonly NatsJSContext _js;

        private readonly ILogger<Worker> _logger;

        private readonly IOptionsMonitor<Data> _dataMonitor;
        private readonly IDisposable? _onDataChange;
        private bool _dataChanged;

        private readonly Dictionary<NameId, CpuRssValue> _measurements;
        private readonly Meter _meter;

        private async Task<KeyValuePair<NameId, CpuRssValue>> DoWorkAsync(Process proc)
        {
            var nameId = new NameId(proc.Id, proc.ProcessName);

            double usageCpuTotal;
            try
            {
                if (proc.HasExited) throw new Exception("process has exited");
                TimeSpan startUsageCpu = proc.TotalProcessorTime;
                long startTime = Environment.TickCount64;

                await Task.Delay(_dataMonitor.CurrentValue.Interval / 2);
                proc.Refresh();

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
                //_logger.LogWarning($"{nameId.Name} : {nameId.Id} => {ex.Message}    CPU");
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

            double rss;
            try
            {
                if (!proc.HasExited)
                    proc.Refresh();
                rss = proc.WorkingSet64 / (1024 * 1024.0);
            }
            catch (Win32Exception ex)
            {
                //_logger.LogWarning($"{nameId.Name} : {nameId.Id} => {ex.Message}    RSS");
                rss = -ex.NativeErrorCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                rss = -404;
            }

            return new KeyValuePair<NameId, CpuRssValue>(nameId,
                                                        new CpuRssValue(usageCpuTotal, rss));
        }
        public Worker(ILogger<Worker> logger, IOptionsMonitor<Data> dataMonitor)
        {
            _nats = new NatsConnection(NatsOpts.Default with{Url = "nats://nats_server:4222" });
            _js = new NatsJSContext(_nats);

            _dataChanged = false;
            _logger = logger;

            _dataMonitor = dataMonitor;
            _onDataChange = _dataMonitor.OnChange(_ => _dataChanged = true);

            _measurements = new();
            _meter = new("cpu_rss_watcher");
        }

        private IEnumerable<Measurement<double>> ObserveValues(Func<CpuRssValue, double> getVal)
        {
            LinkedList<Measurement<double>> result = new();
            foreach (var measurement in _measurements)
            {
                result.AddLast(new Measurement<double>(getVal(measurement.Value),
                    new KeyValuePair<string, object?>("process_name", measurement.Key.Name),
                    new KeyValuePair<string, object?>("process_id", measurement.Key.Id)));
            }

            return result;
        }
        private void ClearMeasurements(CancellationToken stoppingToken)
        {
            var keys = _measurements.Keys;

            Parallel.ForEach(keys, ClearPlusExitedMessageAsync);
            return;

            async void ClearPlusExitedMessageAsync(NameId key)
            {
                try
                {
                    if (_measurements[key].IsUsed)
                    {
                        var ack = await _js.PublishAsync($"{SubjectName}.{key.Name}.{key.Id}", "Exited",
                            cancellationToken: stoppingToken);
                        ack.EnsureSuccess();

                        _measurements.Remove(key);
                    }
                    else
                        _measurements[key].IsUsed = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex.Message);
                }
            }
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                //nats
                INatsJSStream? stream;
                try
                {
                    stream = await _js.GetStreamAsync(StreamName, cancellationToken: stoppingToken);
                    _logger.LogInformation("Stream got successfully");
                }
                catch (NatsJSApiException e) when (e.Error.ErrCode == 10059)
                {
                    stream = await _js.CreateStreamAsync(
                        new StreamConfig(StreamName, new[] { $"{SubjectName}.>" }),
                        stoppingToken);
                    _logger.LogInformation("Stream created successfully");
                }
                catch (Exception e)
                {
                    _logger.LogError(e.Message);
                    throw;
                }

                var goodStreamConfig = new StreamConfig(StreamName, new[] { $"{SubjectName}.>" })
                                            {
                                                MaxMsgsPerSubject = 1
                                            };
                if (stream.Info.Config.Equals(goodStreamConfig))
                {
                    stream.Info.Config = goodStreamConfig;
                    await _js.UpdateStreamAsync(stream.Info.Config, stoppingToken);
                    _logger.LogInformation("Stream config updated successfully");
                }



                //metrics get
                IEnumerable<Measurement<double>> ObserveCpu() => ObserveValues(val => val.Cpu);
                _meter.CreateObservableGauge("worker_processes_usage_cpu", ObserveCpu, unit: "%");

                IEnumerable<Measurement<double>> ObserveRss() => ObserveValues(val => val.Rss);
                _meter.CreateObservableGauge("worker_processes_usage_rss", ObserveRss, unit: "mb");



                //set
                string[] processNames = _dataMonitor.CurrentValue.ProcessNames;
                var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_dataMonitor.CurrentValue.Interval));
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    //going from names to Process
                    var processes = new Process[processNames.Length][];
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
                                DoWorkAsync(processes[i][j]); //starting of the calculating of CPU usage
                        }
                    }

                    ClearMeasurements(stoppingToken);


                    //wait while works
                    foreach (Task[] useCpu in metricsLoop)
                        Task.WaitAll(useCpu, stoppingToken);


                    //add to measurements
                    foreach (var results in metricsLoop)
                    foreach (var result in results)
                    {
                        bool isAdded = false;
                        foreach (var measurement in _measurements)
                        {
                            if (measurement.Key.SequenceEqual(result.Result.Key))
                            {
                                _measurements[measurement.Key] = result.Result.Value;
                                isAdded = true;
                                break;
                            }
                        }

                        if (!isAdded)
                            _measurements.Add(result.Result.Key, result.Result.Value);
                    }


                    //upload to nats
                    Parallel.ForEach(_measurements, UploadToNatsAsync);


                    //on data change
                    if (_dataChanged)
                    {
                        _dataChanged = false;
                        processNames = _dataMonitor.CurrentValue.ProcessNames;
                        timer.Dispose();
                        timer = new(TimeSpan.FromMilliseconds(_dataMonitor.CurrentValue.Interval));
                    }

                    continue;

                    async void UploadToNatsAsync(KeyValuePair<NameId, CpuRssValue> measurement)
                    {
                        try
                        {
                            var ack = await _js.PublishAsync(
                                $"{SubjectName}.{measurement.Key.Name}.{measurement.Key.Id}",
                                $"cpu: {measurement.Value.Cpu} || rss: {measurement.Value.Rss}",
                                cancellationToken: stoppingToken);

                            ack.EnsureSuccess();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex.Message);
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
                _onDataChange?.Dispose();
                await _nats.DisposeAsync();
            }
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
        public bool SequenceEqual( NameId target)
        {
            return Name == target.Name && Id == target.Id;
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
