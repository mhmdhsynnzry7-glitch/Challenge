using System.Collections.Concurrent;
using System.Data.Common;
using Challenge.Api.Data;
using Challenge.Api.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Challenge.Api.Tests;

[CollectionDefinition("Order database mutations", DisableParallelization = true)]
public sealed class OrderDatabaseCollection;

public sealed class OrdersApiFactory : WebApplicationFactory<Program>
{
    private readonly string _apiDirectory;
    private readonly string _connectionString;
    internal StockUpdateGate? InventoryGate { get; set; }
    internal bool FailAfterOrderSave { get; set; }

    public OrdersApiFactory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "Challenge.Api")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Cannot locate the challenge backend.");
        }

        _apiDirectory = Path.Combine(directory.FullName, "src", "Challenge.Api");
        var configuration = new ConfigurationBuilder().SetBasePath(_apiDirectory)
            .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json").Build();
        var connection = new SqlConnectionStringBuilder(configuration.GetConnectionString("DefaultConnection"));
        if (connection.DataSource != "." || connection.InitialCatalog != "ChallengeDb" ||
            !connection.IntegratedSecurity)
        {
            throw new InvalidOperationException("Order tests require the configured local ChallengeDb.");
        }

        _connectionString = connection.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(_apiDirectory).UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AppDbContext>();
            services.AddScoped(_ => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                // A connection string creates an independent SQL connection for each request context.
                .UseSqlServer(_connectionString)
                .AddInterceptors(new StockCommandInterceptor(this), new OrderSaveFailureInterceptor(this))
                .Options));
        });
    }

    internal sealed class StockUpdateGate
    {
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        internal ConcurrentDictionary<Guid, byte> ConnectionIds { get; } = new();
        internal int Arrivals => _arrivals;

        internal async Task ArriveAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            ConnectionIds.TryAdd(((SqlConnection)connection).ClientConnectionId, 0);
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _ready.TrySetResult(true);
            }

            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    private sealed class StockCommandInterceptor(OrdersApiFactory factory) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var gate = factory.InventoryGate;
            if (gate is not null && command.CommandText.Contains("UPDATE") &&
                command.CommandText.Contains("[StockQuantity]"))
            {
                await gate.ArriveAsync(command.Connection!, cancellationToken);
            }

            return result;
        }
    }

    private sealed class OrderSaveFailureInterceptor(OrdersApiFactory factory) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (factory.FailAfterOrderSave && eventData.Context!.ChangeTracker.Entries<Order>().Any())
            {
                throw new InvalidOperationException("Injected failure after order and items were saved.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
