using MesControlAgv.Application;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkflowRuntimeSchemaUpgradeTests
{
    [Fact]
    public async Task Existing_g3_database_adds_runtime_record_tables_without_recreating_prior_data()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"mes-g4-schema-upgrade-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<MesDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using (var setup = new MesDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                await setup.Database.ExecuteSqlRawAsync("DROP TABLE WorkflowDeviceOperations;");
                await setup.Database.ExecuteSqlRawAsync("DROP TABLE WorkflowNodeExecutions;");
            }

            await using (var factory = new ExistingWorkflowDatabaseFactory(databasePath))
            {
                using var client = factory.CreateClient();
                using var health = await client.GetAsync("/health");
                health.EnsureSuccessStatusCode();

                await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
                await connection.OpenAsync();
                var tables = await ReadNamesAsync(
                    connection,
                    "SELECT name FROM sqlite_master WHERE type = 'table';");
                var indexes = await ReadNamesAsync(
                    connection,
                    "SELECT name FROM sqlite_master WHERE type = 'index';");

                Assert.Contains("WorkflowVersions", tables);
                Assert.Contains("WorkflowExecutions", tables);
                Assert.Contains("WorkflowAudits", tables);
                Assert.Contains("WorkflowNodeExecutions", tables);
                Assert.Contains("WorkflowDeviceOperations", tables);
                Assert.Contains("IX_WorkflowNodeExecutions_StepRequestId", indexes);
                Assert.Contains("IX_WorkflowDeviceOperations_NodeExecutionId", indexes);
            }

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
            if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
            if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
        }
    }

    private static async Task<IReadOnlyList<string>> ReadNamesAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    private sealed class ExistingWorkflowDatabaseFactory(string databasePath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MesDbContext>>();
                services.RemoveAll<MesDbContext>();
                services.RemoveAll<IAgvGateway>();
                services.AddDbContext<MesDbContext>(options =>
                    options.UseSqlite($"Data Source={databasePath};Pooling=False"));
                services.AddSingleton<IAgvGateway, TestAdapterClient>();
            });
        }
    }
}
