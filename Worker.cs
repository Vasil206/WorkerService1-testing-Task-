using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace WorkerService1
{
    public class Worker : BackgroundService
    {
        private readonly string _streamsAndSubjectsPrefix;
        private readonly NatsConnection _nats;
        private readonly NatsJSContext _js;

        private readonly ILogger<Worker> _logger;

        private readonly IOptionsMonitor<Data> _dataMonitor;
        private readonly IDisposable? _onDataChange;
        private bool _dataChanged;

        private readonly Dictionary<string, CpuRssValue> _measurements;
        private readonly Meter _meter;


        public Worker(ILogger<Worker> logger, IOptionsMonitor<Data> dataMonitor)
        {
            _streamsAndSubjectsPrefix = WorkerOptions.Default.StreamsAndSubjectsPrefix;
            _nats = new NatsConnection(NatsOpts.Default with{Url = WorkerOptions.Default.NatsConnection });
            _js = new NatsJSContext(_nats);

            _dataChanged = false;
            _logger = logger;

            _dataMonitor = dataMonitor;
            _onDataChange = _dataMonitor.OnChange(_ => _dataChanged = true);

            _measurements = new();
            _meter = new(WorkerOptions.Default.MeterName);
        }


        private async Task<KeyValuePair<NameId, CpuRssValue>> DoWorkAsync(Process proc)
        {
            var nameId = new NameId(proc.Id, proc.ProcessName);


            ////////////////////////////////////////////////////////////////////////////////////////////
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
                _logger.LogWarning($"{nameId.Name} : {nameId.Id} => {ex.Message}    CPU");
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

            ////////////////////////////////////////////////////////////////////////////////////////////
            double rss;
            try
            {
                if (!proc.HasExited)
                    proc.Refresh();
                rss = proc.WorkingSet64 / (1024 * 1024.0);
            }
            catch (Win32Exception ex)
            {
                _logger.LogWarning($"{nameId.Name} : {nameId.Id} => {ex.Message}    RSS");
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


        private IEnumerable<Measurement<double>> ObserveValues(Func<CpuRssValue, double> getVal)
        {
            LinkedList<Measurement<double>> result = new();
            foreach (var measurement in _measurements)
            {
                string[] nameId = measurement.Key
                                            .Remove(0, _streamsAndSubjectsPrefix.Length + 1)
                                            .Split('#');

                result.AddLast(new Measurement<double>(getVal(measurement.Value),
                    new KeyValuePair<string, object?>("process_name", nameId[0]),
                    new KeyValuePair<string, object?>("process_id", nameId[1])));
            }

            return result;
        }

        private void MetricsGetters()
        {
            IEnumerable<Measurement<double>> ObserveCpu() => ObserveValues(val => val.Cpu);
            _meter.CreateObservableGauge("worker_processes_usage_cpu", ObserveCpu, unit: "%");

            IEnumerable<Measurement<double>> ObserveRss() => ObserveValues(val => val.Rss);
            _meter.CreateObservableGauge("worker_processes_usage_rss", ObserveRss, unit: "mb");
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                MetricsGetters();


                ////////////////////////////////////////////////////////////////////////////////////////
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


                    ClearMeasurementsAndStreams(stoppingToken);


                    //wait while works
                    foreach (Task[] useCpu in metricsLoop)
                        Task.WaitAll(useCpu, stoppingToken);


                    //add to measurements   and   set nats streams
                    Parallel.ForEach(metricsLoop, results =>
                        Parallel.ForEach(results, result =>
                            SetStreamAndMeasurementAsync(result.Result, stoppingToken)));

                    UploadToNatsParallel(stoppingToken);


                    //on data change
                    if (_dataChanged)
                    {
                        _dataChanged = false;
                        processNames = _dataMonitor.CurrentValue.ProcessNames;
                        timer.Dispose();
                        timer = new(TimeSpan.FromMilliseconds(_dataMonitor.CurrentValue.Interval));
                    }
                    
                }

                ////////////////////////////////////////////////////////////////////////////////////////
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


        private void ClearMeasurementsAndStreams(CancellationToken stoppingToken)
        {
            Parallel.ForEach(_measurements.Keys, ClearAndDelStream);
            return;

            async void ClearAndDelStream(string key)
            {
                try
                {
                    if (_measurements[key].IsUsed)
                    {
                        
                        _measurements.Remove(key);

                        var streamName = new StringBuilder($"{_streamsAndSubjectsPrefix}.{key}");

                        streamName.Replace('.', '_');
                        streamName.Replace(' ', '_');
                        await _js.DeleteStreamAsync(streamName.ToString(), stoppingToken);
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


        private void UploadToNatsParallel(CancellationToken stoppingToken)
        {
            Parallel.ForEach(_measurements, UploadToNatsAsync);
            return;

            async void UploadToNatsAsync(KeyValuePair<string, CpuRssValue> measurement)
            {
                try
                {
                    var ack = await _js.PublishAsync(
                        measurement.Key,
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

        private async void SetStreamAndMeasurementAsync(KeyValuePair<NameId, CpuRssValue> measurement, CancellationToken stoppingToken)
        {
            StringBuilder streamNameOrSubject = new StringBuilder();
            streamNameOrSubject.Append($"{_streamsAndSubjectsPrefix}.{measurement.Key.ToString()}");
            string subject = streamNameOrSubject.ToString();

            streamNameOrSubject.Replace('.', '_');
            streamNameOrSubject.Replace(' ', '_');
            string name = streamNameOrSubject.ToString();

            try
            {
                INatsJSStream? stream;
                if (_measurements.ContainsKey(subject))
                {
                    _measurements[subject] = measurement.Value;

                    stream = await _js.GetStreamAsync(name, cancellationToken: stoppingToken);
                }
                else
                {
                    _measurements.Add(subject, measurement.Value);

                    stream = await _js.CreateStreamAsync(
                        new StreamConfig(name, new[] { subject }),
                        stoppingToken);
                }

                if (stream.Info.Config.MaxMsgs != 1)
                {
                    stream.Info.Config.MaxMsgs = 1;
                    await stream.UpdateAsync(stream.Info.Config, stoppingToken);
                }

            }
            catch (NatsJSApiException e) when (e.Error.ErrCode == 10058 || e.Error.ErrCode == 10059)
            {
                _logger.LogWarning(e.Message);
            }
            catch (Exception e)
            {
                _logger.LogError(e.Message);
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

        public override string ToString() => $"{Name}#{Id}";
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
