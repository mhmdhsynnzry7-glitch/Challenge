using System.Collections.Concurrent;
using System.Data.Common;
using Challenge.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Challenge.Api.Tests;

public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    private readonly string _apiDirectory;
    private readonly SqlConnection _connection;
    private SqlTransaction? _testTransaction;
    internal ConcurrentQueue<string> ExecutedQueries { get; } = new();

    public AuthApiFactory()
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
        var configuration = new ConfigurationBuilder()
            .SetBasePath(_apiDirectory)
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.Development.json")
            .Build();
        var connection = new SqlConnectionStringBuilder(
            configuration.GetConnectionString("DefaultConnection"));

        if (connection.DataSource != "." || connection.InitialCatalog != "ChallengeDb" ||
            !connection.IntegratedSecurity)
        {
            throw new InvalidOperationException("Auth tests require the configured local ChallengeDb.");
        }

        // Start against the already migrated challenge database, never another application database.
        _connection = new SqlConnection(connection.ConnectionString);
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(_apiDirectory);
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AppDbContext>();
            services.AddScoped(_ =>
            {
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlServer(_connection)
                    .AddInterceptors(new QueryCaptureInterceptor(ExecutedQueries))
                    .Options;
                var db = new AppDbContext(options);
                if (_testTransaction is not null)
                {
                    db.Database.UseTransaction(_testTransaction);
                }

                return db;
            });
        });
    }

    internal async Task BeginTestTransactionAsync()
    {
        _testTransaction = (SqlTransaction)await _connection.BeginTransactionAsync();
    }

    internal async Task RollbackTestTransactionAsync()
    {
        if (_testTransaction is not null)
        {
            await _testTransaction.RollbackAsync();
            await _testTransaction.DisposeAsync();
            _testTransaction = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _testTransaction?.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class QueryCaptureInterceptor(ConcurrentQueue<string> queries) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            queries.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
