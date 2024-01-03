
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

        private void MemoryCpuLoad(long timeExec)
        {
            DateTime start = DateTime.Now;
            long[] arr = new long[100000000];
            long i = 1;
            while (i <= 100000000 && (DateTime.Now - start).TotalMilliseconds <= timeExec) 
            {
                arr[i] = arr[i - 1] + 1;
                i++;
            }
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            PeriodicTimer timer = new(TimeSpan.FromMinutes(3));
            Random rand = new(DateTime.Now.Millisecond);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    long timeExecMilliseconds = rand.Next(1, 170000);
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
