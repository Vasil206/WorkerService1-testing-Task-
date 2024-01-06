
namespace WorkerService1
{
    internal class Load : BackgroundService
    {
        private static void CpuLoad(long timeExec)
        {
            DateTime start = DateTime.Now;
            while ((DateTime.Now-start).TotalMilliseconds<=timeExec)
            {
            }
        }

        private static void MemoryCpuLoad(long timeExec)
        {
            DateTime start = DateTime.Now;
            long[] arr = GC.AllocateArray<long>(100000000);
            long i = 1;
            while (i <= 100000000 && (DateTime.Now - start).TotalMilliseconds <= timeExec) 
            {
                arr[i] = arr[i - 1] + 1;
                i++;
            }

            Task.Delay(Math.Max(Convert.ToInt32(timeExec - (DateTime.Now - start).TotalMilliseconds), 0));
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            PeriodicTimer timer = new(TimeSpan.FromMinutes(3));
            Random rand = new(DateTime.Now.Millisecond);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    long timeExecMilliseconds = rand.Next(1, 160000);
                    if (rand.Next(2) is 1)
                    {
                        CpuLoad(timeExecMilliseconds);
                    }
                    else
                    {
                        MemoryCpuLoad(timeExecMilliseconds);
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
