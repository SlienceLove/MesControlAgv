using MesControlAgv.Application;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using MesControlAgv.Mes.Entities;
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

    [Fact]
    public async Task Existing_g4_database_adds_experiment_scheduling_tables_without_recreating_prior_data()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"mes-g5-schema-upgrade-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<MesDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using (var setup = new MesDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.WorkflowVersions.Add(new WorkflowVersionRecord
                {
                    WorkflowId = Guid.NewGuid(),
                    Version = 1,
                    DefinitionJson = "{}",
                    Status = "Published",
                    PublishStatus = "Published",
                    CreatedBy = "g4-schema-test",
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                await setup.SaveChangesAsync();

                await setup.Database.ExecuteSqlRawAsync("DROP TABLE WorkflowResourceLeases;");
                await setup.Database.ExecuteSqlRawAsync("DROP TABLE ResourceReservations;");
                await setup.Database.ExecuteSqlRawAsync("DROP TABLE ScheduleEntries;");
                await setup.Database.ExecuteSqlRawAsync("DROP TABLE ExperimentJobs;");
                await setup.Database.ExecuteSqlRawAsync("DROP TABLE ExperimentPlans;");
                await setup.Database.ExecuteSqlRawAsync("DROP TABLE ExperimentSchedulingAudits;");
            }

            await using (var factory = new ExistingWorkflowDatabaseFactory(databasePath))
            {
                using var client = factory.CreateClient();
                using var health = await client.GetAsync("/health");
                health.EnsureSuccessStatusCode();
                using var plans = await client.GetAsync("/api/experiment-plans");
                using var schedule = await client.GetAsync("/api/schedule");
                plans.EnsureSuccessStatusCode();
                schedule.EnsureSuccessStatusCode();

                await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
                await connection.OpenAsync();
                var tables = await ReadNamesAsync(
                    connection,
                    "SELECT name FROM sqlite_master WHERE type = 'table';");
                var indexes = await ReadNamesAsync(
                    connection,
                    "SELECT name FROM sqlite_master WHERE type = 'index';");

                Assert.Contains("ExperimentPlans", tables);
                Assert.Contains("ExperimentJobs", tables);
                Assert.Contains("ScheduleEntries", tables);
                Assert.Contains("ResourceReservations", tables);
                Assert.Contains("WorkflowResourceLeases", tables);
                Assert.Contains("ExperimentSchedulingAudits", tables);
                Assert.Contains("IX_ResourceReservations_ResourceKey_StartsAtUtc_EndsAtUtc", indexes);
                Assert.Contains("IX_WorkflowResourceLeases_ActiveResourceKey", indexes);
                Assert.Contains("IX_ExperimentSchedulingAudits_RequestId", indexes);

                await using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM WorkflowVersions;";
                Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
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

    [Fact]
    public async Task Existing_g5a_database_adds_command_columns_and_audit_without_losing_plans()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"mes-g5b-schema-upgrade-{Guid.NewGuid():N}.db");
        var planId = Guid.NewGuid();

        try
        {
            var options = new DbContextOptionsBuilder<MesDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using (var setup = new MesDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.ExperimentPlans.Add(new ExperimentPlanRecord
                {
                    PlanId = planId,
                    Version = 1,
                    Name = "Retained G5-A plan",
                    Description = "Upgrade evidence",
                    WorkflowId = Guid.NewGuid(),
                    WorkflowVersion = 1,
                    Status = "Draft",
                    MaterialRequirementsJson = "[]",
                    DefaultParametersJson = "{}",
                    ResourceRequirementsJson = "[]",
                    ProfileProductId = "MES-AGV",
                    ProfileVersion = "1.0",
                    CreatedBy = "g5a-upgrade-test",
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                await setup.SaveChangesAsync();

                await setup.Database.ExecuteSqlRawAsync("DROP TABLE ExperimentSchedulingAudits;");
                await setup.Database.ExecuteSqlRawAsync("ALTER TABLE ScheduleEntries DROP COLUMN RequestedResourcesJson;");
                await setup.Database.ExecuteSqlRawAsync("ALTER TABLE ExperimentPlans DROP COLUMN ValidatedAtUtc;");
                await setup.Database.ExecuteSqlRawAsync("ALTER TABLE ExperimentPlans DROP COLUMN ValidatedBy;");
                await setup.Database.ExecuteSqlRawAsync("ALTER TABLE ExperimentPlans DROP COLUMN ValidationJson;");
            }

            await using (var factory = new ExistingWorkflowDatabaseFactory(databasePath))
            {
                using var client = factory.CreateClient();
                using var health = await client.GetAsync("/health");
                health.EnsureSuccessStatusCode();
                using var plan = await client.GetAsync($"/api/experiment-plans/{planId}/versions/1");
                plan.EnsureSuccessStatusCode();

                await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
                await connection.OpenAsync();
                var tables = await ReadNamesAsync(
                    connection,
                    "SELECT name FROM sqlite_master WHERE type = 'table';");
                var indexes = await ReadNamesAsync(
                    connection,
                    "SELECT name FROM sqlite_master WHERE type = 'index';");
                var planColumns = await ReadNamesAsync(
                    connection,
                    "SELECT name FROM pragma_table_info('ExperimentPlans');");
                var scheduleColumns = await ReadNamesAsync(
                    connection,
                    "SELECT name FROM pragma_table_info('ScheduleEntries');");

                Assert.Contains("ExperimentSchedulingAudits", tables);
                Assert.Contains("IX_ExperimentSchedulingAudits_RequestId", indexes);
                Assert.Contains("ValidationJson", planColumns);
                Assert.Contains("ValidatedBy", planColumns);
                Assert.Contains("ValidatedAtUtc", planColumns);
                Assert.Contains("RequestedResourcesJson", scheduleColumns);

                await using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM ExperimentPlans;";
                Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
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
