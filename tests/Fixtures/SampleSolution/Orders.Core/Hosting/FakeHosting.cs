using System.Threading;
using System.Threading.Tasks;

namespace Orders.Hosting;

/// <summary>Stand-in for Microsoft.Extensions.Hosting.BackgroundService — see FakeMvc.cs for why fakes work here.</summary>
public abstract class BackgroundService
{
    protected abstract Task ExecuteAsync(CancellationToken stoppingToken);
}
