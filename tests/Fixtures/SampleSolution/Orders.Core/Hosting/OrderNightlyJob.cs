using System.Threading;
using System.Threading.Tasks;
using Orders.Data;

namespace Orders.Hosting;

/// <summary>Fixture for the `job` entry point kind (spec section 9's fixture list: "một BackgroundService").</summary>
public class OrderNightlyJob : BackgroundService
{
    private readonly OrderRepository _orderRepository;

    public OrderNightlyJob(OrderRepository repository)
    {
        _orderRepository = repository;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _orderRepository.Exists(0);
        await Task.CompletedTask;
    }
}
