using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace DdoDatApi.Caching;

public abstract class ExportService : BackgroundService
{
    private readonly SemaphoreSlim _signal = new SemaphoreSlim(0); // Used to signal a new task is ready

    public bool IsRunning { get; private set; }

    public void Export()
    {
        _signal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(stoppingToken); // Wait for a signal from the controller
            IsRunning = true;
            try
            {
                await Export(stoppingToken);
            }
            finally
            {
                IsRunning = false;
            }
        }
    }

    public abstract Task Export(CancellationToken token);
}
