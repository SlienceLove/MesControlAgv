using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class MaterialManagementApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly MesWebApplicationFactory _factory;

    public MaterialManagementApiTests(MesWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Import_scan_and_inventory_queries_are_atomic_and_idempotent()
    {
        using var client = _factory.CreateClient();
        var requestId = Guid.NewGuid();
        var import = new MaterialImportRequest
        {
            RequestId = requestId,
            Actor = "api-test",
            Samples = new[]
            {
                new SampleImportRow
                {
                    RowNumber = 1,
                    Barcode = "sample-api-001",
                    SampleBatchId = "batch-api-001",
                    SampleType = "water"
                }
            },
            Consumables = new[]
            {
                new ConsumableImportRow
                {
                    RowNumber = 1,
                    MaterialCode = "buffer-api",
                    Name = "Buffer",
                    Unit = "mL",
                    LotCode = "lot-api-001",
                    Quantity = 10
                }
            }
        };

        var preview = await client.PostAsJsonAsync("/api/materials/import/preview", import);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewBody = await preview.Content.ReadFromJsonAsync<MaterialImportPreview>();
        Assert.NotNull(previewBody);
        Assert.True(previewBody!.CanCommit);

        var first = await client.PostAsJsonAsync("/api/materials/import", import);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<MaterialCommandResult<MaterialImportResult>>();
        Assert.NotNull(firstBody);
        Assert.False(firstBody!.IsIdempotentReplay);
        Assert.Equal(1, firstBody.Data!.ImportedSamples);
        Assert.Equal(1, firstBody.Data.ImportedConsumableLots);

        var replay = await client.PostAsJsonAsync("/api/materials/import", import);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<MaterialCommandResult<MaterialImportResult>>();
        Assert.NotNull(replayBody);
        Assert.True(replayBody!.IsIdempotentReplay);
        Assert.Equal(firstBody.Data.ImportedSamples, replayBody.Data!.ImportedSamples);

        var inventory = await client.GetFromJsonAsync<List<MaterialLotInventory>>(
            "/api/materials/inventory?materialCode=BUFFER-API");
        Assert.NotNull(inventory);
        Assert.Single(inventory!);
        Assert.Equal(10m, inventory[0].OnHand);
        Assert.Equal(10m, inventory[0].Available);

        var scanRequest = new MaterialScanRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "api-test",
            RawCode = "sample-api-001",
            Kind = MaterialScanKind.Sample
        };
        var scan = await client.PostAsJsonAsync("/api/materials/scan", scanRequest);
        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        var scanBody = await scan.Content.ReadFromJsonAsync<MaterialScanResult>();
        Assert.NotNull(scanBody);
        Assert.True(scanBody!.IsResolved);
        Assert.Equal(MaterialScanKind.Sample, scanBody.Kind);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Single(database.SampleMaterials);
        Assert.Single(database.MaterialLots);
        Assert.Equal(2, database.InventoryTransactions.Count());
        Assert.Single(database.BarcodeScanEvents);
    }

    [Fact]
    public async Task Receive_move_adjust_and_request_reuse_are_mapped_to_business_errors()
    {
        using var client = _factory.CreateClient();
        var receive = new ReceiveMaterialRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "api-test",
            MaterialCode = "move-api",
            MaterialName = "Move material",
            LotCode = "lot-move",
            Unit = "EA",
            Quantity = 4,
            LocationCode = "DEFAULT"
        };
        var received = await client.PostAsJsonAsync("/api/materials/receive", receive);
        Assert.Equal(HttpStatusCode.Created, received.StatusCode);
        var receivedBody = await received.Content.ReadFromJsonAsync<MaterialCommandResult<MaterialLotInventory>>();
        var lotId = receivedBody!.Data!.LotId;

        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var now = DateTime.UtcNow;
            database.WarehouseLocations.Add(new WarehouseLocationRecord
            {
                LocationId = Guid.NewGuid(),
                WarehouseCode = "MAIN",
                WarehouseName = "默认仓库",
                LocationCode = "SECONDARY",
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            database.SaveChanges();
        }

        var move = new MoveMaterialRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "api-test",
            LotId = lotId,
            FromLocationCode = "DEFAULT",
            ToLocationCode = "SECONDARY",
            Quantity = 2
        };
        var moved = await client.PostAsJsonAsync("/api/materials/move", move);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var movedBody = await moved.Content.ReadFromJsonAsync<MaterialCommandResult<MaterialLotInventory>>();
        Assert.Equal(2m, movedBody!.Data!.OnHand);

        var adjusted = new AdjustMaterialRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "api-test",
            LotId = lotId,
            LocationCode = "SECONDARY",
            QuantityDelta = -3,
            Reason = "bad adjustment"
        };
        var rejected = await client.PostAsJsonAsync("/api/materials/adjust", adjusted);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MaterialIssueCodes.InventoryInsufficient, error.GetProperty("code").GetString());

        var reused = receive with { MaterialName = "changed" };
        var reuseResponse = await client.PostAsJsonAsync("/api/materials/receive", reused);
        Assert.Equal(HttpStatusCode.Conflict, reuseResponse.StatusCode);
        var reuseError = await reuseResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MaterialIssueCodes.RequestIdReused, reuseError.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reservation_release_and_consume_update_available_inventory()
    {
        using var client = _factory.CreateClient();
        var receive = new ReceiveMaterialRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "api-test",
            MaterialCode = "reserve-api",
            MaterialName = "Reserve material",
            LotCode = "lot-reserve",
            Unit = "mL",
            Quantity = 8,
            LocationCode = "DEFAULT"
        };
        var receiveResponse = await client.PostAsJsonAsync("/api/materials/receive", receive);
        receiveResponse.EnsureSuccessStatusCode();
        var receiveBody = await receiveResponse.Content.ReadFromJsonAsync<MaterialCommandResult<MaterialLotInventory>>();
        Assert.NotNull(receiveBody?.Data);

        var jobId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            database.ExperimentJobs.Add(new ExperimentJobRecord
            {
                JobId = jobId,
                PlanId = Guid.NewGuid(),
                PlanVersion = 1,
                WorkflowId = Guid.NewGuid(),
                WorkflowVersion = 1,
                SampleBatchId = "reserve-batch",
                Status = "Scheduled",
                CreatedBy = "api-test",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            database.SaveChanges();
        }

        var reserve = new ReserveExperimentMaterialsRequest
        {
            RequestId = Guid.NewGuid(),
            ExperimentJobId = jobId,
            Actor = "api-test",
            Requirements = new[]
            {
                new MaterialReservationLine
                {
                    MaterialCode = "reserve-api",
                    Quantity = 3,
                    Unit = "mL"
                }
            }
        };
        var reserved = await client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/materials/reserve", reserve);
        Assert.Equal(HttpStatusCode.Created, reserved.StatusCode);
        var reservedBody = await reserved.Content.ReadFromJsonAsync<MaterialReservationResult>();
        Assert.NotNull(reservedBody);
        Assert.Single(reservedBody!.Bindings);

        var inventory = await client.GetFromJsonAsync<List<MaterialLotInventory>>("/api/materials/inventory?materialCode=RESERVE-API");
        Assert.Equal(8m, inventory![0].OnHand);
        Assert.Equal(3m, inventory[0].Reserved);
        Assert.Equal(5m, inventory[0].Available);

        var consume = new ConsumeExperimentMaterialsRequest
        {
            RequestId = Guid.NewGuid(),
            ExperimentJobId = jobId,
            Actor = "api-test",
            InjectionPosition = "18I-01",
            Materials = Array.Empty<MaterialReservationLine>()
        };
        var consumed = await client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/materials/consume", consume);
        Assert.Equal(HttpStatusCode.OK, consumed.StatusCode);
        var consumedBody = await consumed.Content.ReadFromJsonAsync<MaterialConsumeResult>();
        Assert.Equal(1, consumedBody!.ConsumedCount);

        inventory = await client.GetFromJsonAsync<List<MaterialLotInventory>>("/api/materials/inventory?materialCode=RESERVE-API");
        Assert.Equal(5m, inventory![0].OnHand);
        Assert.Equal(0m, inventory[0].Reserved);
        Assert.Equal(5m, inventory[0].Available);

        var secondReserve = reserve with
        {
            RequestId = Guid.NewGuid(),
            Requirements = new[]
            {
                new MaterialReservationLine { MaterialCode = "reserve-api", Quantity = 2, Unit = "mL" }
            }
        };
        var second = await client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/materials/reserve", secondReserve);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var release = new ReleaseExperimentMaterialsRequest
        {
            RequestId = Guid.NewGuid(),
            ExperimentJobId = jobId,
            Actor = "api-test",
            Reason = "cancelled"
        };
        var released = await client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/materials/release", release);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        var releasedBody = await released.Content.ReadFromJsonAsync<MaterialReleaseResult>();
        Assert.Equal(1, releasedBody!.ReleasedCount);
    }
    [Fact]
    public async Task Quarantined_lot_scan_is_rejected_with_quarantine_code()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/materials/receive", new ReceiveMaterialRequest
        {
            RequestId = Guid.NewGuid(), Actor = "api-test", MaterialCode = "LIFECYCLE-" + Guid.NewGuid().ToString("N"), MaterialName = "Lifecycle",
            LotCode = "LOT-QUAR", Quantity = 4, Unit = "EA", Barcode = "BC-QUAR-" + Guid.NewGuid().ToString("N")
        });
        response.EnsureSuccessStatusCode();
        var received = (await response.Content.ReadFromJsonAsync<MaterialCommandResult<MaterialLotInventory>>())!.Data!;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var lot = await db.MaterialLots.SingleAsync(item => item.LotId == received.LotId);
            lot.IsQuarantined = true;
            await db.SaveChangesAsync();
        }
        var scan = await client.PostAsJsonAsync("/api/materials/scan", new MaterialScanRequest
        { RequestId = Guid.NewGuid(), Actor = "api-test", RawCode = received.Barcode!, Kind = MaterialScanKind.ConsumableLot });
        var scanBody = await scan.Content.ReadFromJsonAsync<MaterialScanResult>();
        Assert.False(scanBody!.IsResolved);
        Assert.Equal(MaterialIssueCodes.Quarantined, scanBody.IssueCode);
        var move = await client.PostAsJsonAsync("/api/materials/move", new MoveMaterialRequest
        { RequestId = Guid.NewGuid(), Actor = "api-test", LotId = received.LotId, ToLocationCode = "DEFAULT", Quantity = 1 });
        var moveError = await move.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MaterialIssueCodes.Quarantined, moveError.GetProperty("code").GetString());

        response = await client.PostAsJsonAsync("/api/materials/receive", new ReceiveMaterialRequest
        {
            RequestId = Guid.NewGuid(), Actor = "api-test", MaterialCode = "LIFECYCLE-EXP-" + Guid.NewGuid().ToString("N"), MaterialName = "Expired",
            LotCode = "LOT-EXP", Quantity = 1, Unit = "EA", Barcode = "BC-EXP-" + Guid.NewGuid().ToString("N"), ExpiryDate = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        response.EnsureSuccessStatusCode();
        received = (await response.Content.ReadFromJsonAsync<MaterialCommandResult<MaterialLotInventory>>())!.Data!;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var lot = await db.MaterialLots.SingleAsync(item => item.LotId == received.LotId);
            lot.ExpiryDateUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        var adjust = await client.PostAsJsonAsync("/api/materials/adjust", new AdjustMaterialRequest
        { RequestId = Guid.NewGuid(), Actor = "api-test", LotId = received.LotId, QuantityDelta = 1, Reason = "expired" });
        var adjustError = await adjust.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MaterialIssueCodes.Expired, adjustError.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Trace_by_job_returns_binding_lot_identity()
    {
        using var client = _factory.CreateClient();
        var code = "TRACE-" + Guid.NewGuid().ToString("N");
        var receive = await client.PostAsJsonAsync("/api/materials/receive", new ReceiveMaterialRequest
        { RequestId = Guid.NewGuid(), Actor = "api-test", MaterialCode = code, MaterialName = "Trace", LotCode = "TRACE-LOT", Barcode = "TRACE-BC", Quantity = 2, Unit = "EA" });
        receive.EnsureSuccessStatusCode();
        var jobId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            db.ExperimentJobs.Add(new ExperimentJobRecord { JobId = jobId, PlanId = Guid.NewGuid(), PlanVersion = 1, WorkflowId = Guid.NewGuid(), WorkflowVersion = 1, Status = "Scheduled", CreatedBy = "test", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var reserve = await client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/materials/reserve", new ReserveExperimentMaterialsRequest
        { RequestId = Guid.NewGuid(), ExperimentJobId = jobId, Actor = "api-test", Requirements = new[] { new MaterialReservationLine { MaterialCode = code, LotCode = "TRACE-LOT", Quantity = 1, Unit = "EA" } } });
        reserve.EnsureSuccessStatusCode();
        var reserveBody = await reserve.Content.ReadFromJsonAsync<MaterialReservationResult>();
        var traceLotId = reserveBody!.Bindings[0].LotId!.Value;

        Guid sampleScanId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var lot = await db.MaterialLots.SingleAsync(item => item.LotId == traceLotId);
            lot.MaterialCode = lot.MaterialCode.ToLowerInvariant();
            lot.LotCode = lot.LotCode.ToLowerInvariant();
            lot.Barcode = lot.Barcode!.ToLowerInvariant();
            foreach (var transaction in await db.InventoryTransactions.Where(item => item.LotId == lot.LotId).ToListAsync())
            {
                transaction.MaterialCode = transaction.MaterialCode?.ToLowerInvariant();
                transaction.LotCode = transaction.LotCode?.ToLowerInvariant();
                transaction.Barcode = transaction.Barcode?.ToLowerInvariant();
            }

            var sampleId = Guid.NewGuid();
            var scanRequestId = Guid.NewGuid();
            db.SampleMaterials.Add(new SampleMaterialRecord
            {
                SampleId = sampleId,
                Barcode = "TRACE-SAMPLE-" + Guid.NewGuid().ToString("N"),
                SampleBatchId = "trace-scan",
                MaterialCode = lot.MaterialCode,
                Status = "Available",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.MaterialOperations.Add(new MaterialOperationRecord
            {
                RequestId = scanRequestId,
                OperationKind = "scan",
                Fingerprint = "trace-scan",
                Outcome = "accepted",
                Actor = "api-test",
                CreatedAtUtc = DateTime.UtcNow
            });
            var scan = new BarcodeScanEventRecord
            {
                Id = Guid.NewGuid(),
                RequestId = scanRequestId,
                RawCode = lot.Barcode,
                NormalizedCode = lot.Barcode,
                ScanKind = "Sample",
                Source = "test",
                Outcome = "Resolved",
                SampleId = sampleId,
                Actor = "api-test",
                OccurredAtUtc = DateTime.UtcNow
            };
            sampleScanId = scan.Id;
            db.BarcodeScanEvents.Add(scan);
            await db.SaveChangesAsync();
        }

        var trace = await client.GetFromJsonAsync<List<MaterialTraceEvent>>(
            "/api/materials/trace?barcode=TRACE-BC&materialCode=" + code.ToUpperInvariant() + "&lotCode=TRACE-LOT");
        Assert.Contains(trace!, item => item.MaterialCode == code.ToLowerInvariant() && item.LotCode == "trace-lot" && item.Barcode == "trace-bc");
        Assert.DoesNotContain(trace!, item => item.Id == sampleScanId);

        var jobTrace = await client.GetFromJsonAsync<List<MaterialTraceEvent>>($"/api/materials/trace?experimentJobId={jobId}");
        Assert.Contains(jobTrace!, item => item.MaterialCode == code.ToLowerInvariant() && item.LotCode == "trace-lot" && item.Barcode == "trace-bc");
        Assert.DoesNotContain(jobTrace!, item => item.Id == sampleScanId);
    }

    [Fact]
    public async Task Import_preview_rejects_cross_type_barcode_conflict()
    {
        using var client = _factory.CreateClient();
        var barcode = "CROSS-" + Guid.NewGuid().ToString("N");
        var sample = new MaterialImportRequest
        {
            RequestId = Guid.NewGuid(), Actor = "api-test",
            Samples = new[] { new SampleImportRow { Barcode = barcode, SampleBatchId = "cross-batch" } }
        };
        (await client.PostAsJsonAsync("/api/materials/import", sample)).EnsureSuccessStatusCode();
        var conflicting = sample with
        {
            RequestId = Guid.NewGuid(), Samples = Array.Empty<SampleImportRow>(),
            Consumables = new[] { new ConsumableImportRow { MaterialCode = "CROSS-MAT", Name = "Cross", LotCode = "CROSS-LOT", Quantity = 1, Barcode = barcode } }
        };
        var previewResponse = await client.PostAsJsonAsync("/api/materials/import/preview", conflicting);
        var preview = await previewResponse.Content.ReadFromJsonAsync<MaterialImportPreview>();
        Assert.Contains(preview!.Issues, issue => issue.Code == MaterialIssueCodes.BarcodeAlreadyExists);
    }
}
