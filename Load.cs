
namespace WorkerService1
{
    internal class Load : BackgroundService
    {
        private static void CpuLoad(long timeExec)
        {
            DateTime start = DateTime.Now;
            while ((DateTime.Now-start).TotalSeconds<=timeExec)
            {
            }
        }

        private static void MemoryCpuLoad(long timeExec)
        {
            DateTime start = DateTime.Now;
            List<long> arr = new List<long>(100000000);
            int i = 1;
            while (i <= 100000000 && (DateTime.Now - start).TotalSeconds <= timeExec) 
            {
                arr[i] = arr[i - 1] + 1;
                i++;
            }

            arr.Clear();
            Task.Delay(Math.Max(Convert.ToInt32(timeExec - (DateTime.Now - start).TotalSeconds)*1000, 0));
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            PeriodicTimer timer = new(TimeSpan.FromMinutes(3));
            Random rand = new(DateTime.Now.Millisecond);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    long timeExecSeconds = rand.Next(1, 170);
                    if (rand.Next(2) is 1)
                    {
                        CpuLoad(timeExecSeconds);
                    }
                    else
                    {
                        MemoryCpuLoad(timeExecSeconds);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        }
    }
}
