using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Access;
using MesControlAgv.Mes.Data;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Mes.Tests;

public sealed class AccessBusinessIntegrationTests
{
    [Fact]
    public async Task Enabled_access_protects_real_material_routes_and_uses_logged_in_workflow_permissions()
    {
        var accountPath = Path.Combine(Path.GetTempPath(), $"mes-access-integration-{Guid.NewGuid():N}.db");
        var businessPath = Path.Combine(Path.GetTempPath(), $"mes-business-integration-{Guid.NewGuid():N}.db");
        try
        {
            using var factory = new MesWebApplicationFactory(businessPath);
            using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<AccessSettings>();
                services.AddSingleton(new AccessSettings
                {
                    Enabled = true, Demo = true, DatabasePath = accountPath,
                    BootstrapPassword = "DemoAdmin2026", DemoPassword = "DemoUser2026"
                });
            }));
            using var client = configured.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/materials/inventory")).StatusCode);
            var loginResponse = await client.PostAsJsonAsync("/api/access/login", new AccessLoginRequest("operator", "DemoUser2026"));
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
            var login = (await loginResponse.Content.ReadFromJsonAsync<AccessLoginResponse>())!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
            var importResponse = await client.PostAsJsonAsync("/api/materials/import", new MaterialImportRequest
            {
                RequestId = Guid.NewGuid(), Actor = "forged-admin",
                Samples = [new SampleImportRow { RowNumber = 1, Barcode = "access-sample", SampleBatchId = "access-batch", SampleType = "water" }]
            });
            Assert.Equal(HttpStatusCode.Created, importResponse.StatusCode);
            using var scope = configured.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            Assert.Equal("operator", Assert.Single(database.InventoryTransactions).Actor);
            var permissions = (await client.GetFromJsonAsync<WorkflowRunControlPermissionsSnapshot>("/api/workflow-run-controls/permissions?actor=admin"))!;
            Assert.Equal("operator", permissions.Actor);
            Assert.Contains("workflow.pause", permissions.Permissions);
            Assert.DoesNotContain("workflow.resolve-unknown", permissions.Permissions);
        }
        finally
        {
            foreach (var path in new[] { accountPath, businessPath })
            {
                using var pool = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
                SqliteConnection.ClearPool(pool);
                foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
            }
        }
    }
}
