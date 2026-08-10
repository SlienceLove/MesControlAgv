using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class RuntimeReadinessApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly HttpClient _client;

    public RuntimeReadinessApiTests(MesWebApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Readiness_exposes_profile_flags_stations_directed_edges_and_sha256_fingerprints()
    {
        var response = await _client.GetAsync("/api/runtime/readiness");

        response.EnsureSuccessStatusCode();
        var readiness = await response.Content.ReadFromJsonAsync<RuntimeReadinessResponse>();

        Assert.NotNull(readiness);
        Assert.Equal("MES-AGV", readiness.ProductId);
        Assert.Equal("AGV MES", readiness.ProductName);
        Assert.Equal("1.0", readiness.ProductVersion);
        Assert.True(readiness.UseSimulator);
        Assert.True(readiness.AutomaticDispatchEnabled);
        Assert.True(readiness.TaskCancellationEnabled);
        Assert.Matches("^[0-9a-f]{64}$", readiness.ProfileFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", readiness.MapFingerprint);
        Assert.Contains("SAMPLE_01", readiness.StationIds);
        Assert.Contains(readiness.DirectedEdges, edge => edge is { From: "SAMPLE_01", To: "ST_OPEN_01", Cost: 1 });
        Assert.Contains(readiness.DirectedEdges, edge => edge is { From: "ST_OPEN_01", To: "SAMPLE_01", Cost: 1 });
        Assert.Null(readiness.ExpectedPhysicalMapName);
        Assert.Null(readiness.ExpectedPhysicalMapVersion);
        Assert.Null(readiness.ExpectedPhysicalMapMd5);
    }

    [Fact]
    public async Task Repeated_readiness_reads_have_identical_fingerprints_and_graph_order()
    {
        var first = await _client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/runtime/readiness");
        var second = await _client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/runtime/readiness");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.ProfileFingerprint, second.ProfileFingerprint);
        Assert.Equal(first.MapFingerprint, second.MapFingerprint);
        Assert.Equal(first.StationIds, second.StationIds);
        Assert.Equal(first.DirectedEdges, second.DirectedEdges);
    }

    [Fact]
    public async Task Readiness_endpoint_is_read_only()
    {
        var response = await _client.PostAsync("/api/runtime/readiness", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public void Fingerprints_change_for_profile_and_effective_map_changes()
    {
        var baselineProfile = ProfileConfiguration.Default;
        var baseline = new RuntimeReadinessService(baselineProfile, AgvMap.FromProfile(baselineProfile.Map)).Get();

        var changedProfile = baselineProfile with
        {
            Product = baselineProfile.Product with { Version = "1.1" }
        };
        var changedProduct = new RuntimeReadinessService(changedProfile, AgvMap.FromProfile(changedProfile.Map)).Get();
        Assert.NotEqual(baseline.ProfileFingerprint, changedProduct.ProfileFingerprint);
        Assert.Equal(baseline.MapFingerprint, changedProduct.MapFingerprint);

        var changedMapProfile = baselineProfile with
        {
            Map = baselineProfile.Map with
            {
                Edges = baselineProfile.Map.Edges
                    .Select(edge => edge.From == "SAMPLE_01" && edge.To == "ST_OPEN_01"
                        ? edge with { Cost = edge.Cost + 0.5 }
                        : edge)
                    .ToArray()
            }
        };
        var changedMap = new RuntimeReadinessService(changedMapProfile, AgvMap.FromProfile(changedMapProfile.Map)).Get();
        Assert.NotEqual(baseline.ProfileFingerprint, changedMap.ProfileFingerprint);
        Assert.NotEqual(baseline.MapFingerprint, changedMap.MapFingerprint);
    }
}
