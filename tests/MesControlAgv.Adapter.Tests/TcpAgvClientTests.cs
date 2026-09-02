using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MesControlAgv.Adapter;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Adapter.Tests;

public sealed class TcpAgvClientTests
{
    [Fact]
    public async Task Get_io_reads_vendor_1013_and_preserves_di_validity_and_do_state()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1013, packet.ApiId);
            Assert.Empty(packet.Payload);
            return Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"DI\":[{\"id\":0,\"source\":\"normal\",\"status\":true,\"valid\":true}],\"DO\":[{\"id\":6,\"source\":\"normal\",\"status\":false}]}"));
        });
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = statusServer.Port,
                OtherPort = statusServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        var snapshot = await client.GetIoAsync(cancellation.Token);

        var di = Assert.Single(snapshot.DigitalInputs);
        Assert.Equal(0, di.Id);
        Assert.Equal("normal", di.Source);
        Assert.True(di.Status);
        Assert.True(di.Valid);
        var @do = Assert.Single(snapshot.DigitalOutputs);
        Assert.Equal(6, @do.Id);
        Assert.False(@do.Status);
        Assert.Null(@do.Valid);
        Assert.Equal([1013], statusServer.ApiIds);
        await statusServer.Completion;
    }

    [Fact]
    public async Task Set_do_sends_exact_vendor_6001_payload_on_other_channel_after_ownership_check()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1060, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}"));
        });
        await using var otherServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)6001, packet.ApiId);
            using var request = JsonDocument.Parse(packet.Payload);
            Assert.Equal(6, request.RootElement.GetProperty("id").GetInt32());
            Assert.True(request.RootElement.GetProperty("status").GetBoolean());
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
        });
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = statusServer.Port,
                OtherPort = otherServer.Port,
                EnablePush = false,
                AcquireControl = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.SetDoAsync(6, true, cancellation.Token);

        Assert.Equal(6, result.Id);
        Assert.True(result.Status);
        Assert.Equal(0, result.ReturnCode);
        Assert.Equal([1060], statusServer.ApiIds);
        Assert.Equal([6001], otherServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, otherServer.Completion);
    }

    [Fact]
    public async Task Set_do_is_rejected_in_read_only_preflight_before_any_channel_is_opened()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(0, _ =>
            throw new InvalidOperationException("The status channel must not be opened."));
        await using var otherServer = new TcpApiTestServer(0, _ =>
            throw new InvalidOperationException("The other channel must not be opened."));
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = statusServer.Port,
                OtherPort = otherServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance,
            AdapterRunMode.ReadOnlyPreflight);

        await Assert.ThrowsAsync<ReadOnlyPreflightModeException>(
            () => client.SetDoAsync(6, true, cancellation.Token));

        Assert.False(statusServer.HasPendingConnection);
        Assert.False(otherServer.HasPendingConnection);
        Assert.Empty(statusServer.ApiIds);
        Assert.Empty(otherServer.ApiIds);
    }

    [Fact]
    public async Task Read_only_preflight_uses_only_read_apis_and_rejects_mutations()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(5, HandleStatusAsync);
        var options = CreateOptions(statusServer.Port, statusServer.Port).Value;
        options.EnablePush = true;
        using var client = new TcpAgvClient(
            Options.Create(options),
            NullLogger<TcpAgvClient>.Instance,
            AdapterRunMode.ReadOnlyPreflight);

        await client.StartAsync(cancellation.Token);
        _ = await client.GetSnapshotAsync(cancellation.Token);
        var readiness = await client.GetSafetyReadinessAsync(cancellation.Token);

        await Assert.ThrowsAsync<ReadOnlyPreflightModeException>(
            () => client.EnsureControlAsync(cancellation.Token));
        await Assert.ThrowsAsync<ReadOnlyPreflightModeException>(
            () => client.ReleaseControlAsync(cancellation.Token));
        await Assert.ThrowsAsync<ReadOnlyPreflightModeException>(
            () => client.NavigateAsync(Guid.NewGuid(), "LM1", "LM2", cancellation.Token));
        await Assert.ThrowsAsync<ReadOnlyPreflightModeException>(
            () => client.PauseAsync(Guid.NewGuid(), cancellation.Token));
        await Assert.ThrowsAsync<ReadOnlyPreflightModeException>(
            () => client.ResumeAsync(Guid.NewGuid(), cancellation.Token));
        await Assert.ThrowsAsync<ReadOnlyPreflightModeException>(
            () => client.CancelAsync(Guid.NewGuid(), cancellation.Token));

        await client.StopAsync(cancellation.Token);
        Assert.Equal("automatic", readiness.VehicleOperatingMode);
        Assert.Equal("vendor-1101-mode", readiness.VehicleOperatingModeSource);
        Assert.Equal("W500-SZ", readiness.VehicleModel);
        Assert.Equal("v3.4.8.0011", readiness.ControllerVersion);
        Assert.Equal(1, readiness.RelocationStatus);
        Assert.Equal([1060, 1110, 1101, 1021, 1000], statusServer.ApiIds);
        await statusServer.Completion;
    }

    [Fact]
    public async Task Read_only_preflight_rejects_control_release_before_opening_any_channel()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(0, _ =>
            throw new InvalidOperationException("The status channel must not be opened."));
        await using var controlServer = new TcpApiTestServer(0, _ =>
            throw new InvalidOperationException("The control channel must not be opened."));
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = controlServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance,
            AdapterRunMode.ReadOnlyPreflight);

        await Assert.ThrowsAsync<ReadOnlyPreflightModeException>(
            () => client.ReleaseControlAsync(cancellation.Token));

        Assert.False(statusServer.HasPendingConnection);
        Assert.False(controlServer.HasPendingConnection);
        Assert.Empty(statusServer.ApiIds);
        Assert.Empty(controlServer.ApiIds);
    }

    [Fact]
    public async Task Release_control_does_not_send_4006_when_another_owner_holds_control()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1060, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"robot-test-software\"}"));
        });
        await using var controlServer = new TcpApiTestServer(0, _ =>
            throw new InvalidOperationException("The control channel must not be opened."));
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = controlServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        var released = await client.ReleaseControlAsync(cancellation.Token);

        Assert.False(released);
        Assert.Equal([1060], statusServer.ApiIds);
        Assert.Empty(controlServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Theory]
    [InlineData("{\"ret_code\":0}")]
    [InlineData("{\"ret_code\":0,\"locked\":null}")]
    [InlineData("{\"ret_code\":0,\"locked\":\"unknown\"}")]
    public async Task Acquire_control_fails_closed_when_1060_locked_state_is_invalid(string payload)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1060, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes(payload));
        });
        await using var controlServer = new TcpApiTestServer(0, _ =>
            throw new InvalidOperationException("4005 must not be sent."));
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = controlServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        await Assert.ThrowsAsync<AgvProtocolException>(() =>
            client.EnsureControlWithResultAsync(cancellation.Token));

        Assert.Empty(controlServer.ApiIds);
        await statusServer.Completion;
    }

    [Fact]
    public async Task Ensure_control_reports_existing_adapter_ownership_without_sending_4005()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1060, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}"));
        });
        await using var controlServer = new TcpApiTestServer(0, _ =>
            throw new InvalidOperationException("4005 must not be sent for existing ownership."));
        using var client = new TcpAgvClient(
            CreateControlOptions(statusServer.Port, controlServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.EnsureControlWithResultAsync(cancellation.Token);

        Assert.False(result.AcquiredByThisCall);
        Assert.Equal([1060], statusServer.ApiIds);
        Assert.Empty(controlServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Ensure_control_reports_new_ownership_after_4005_and_1060_confirmation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ownershipQuery = 0;
        await using var statusServer = new TcpApiTestServer(2, packet =>
        {
            Assert.Equal((ushort)1060, packet.ApiId);
            var payload = Interlocked.Increment(ref ownershipQuery) == 1
                ? "{\"ret_code\":0,\"locked\":false}"
                : "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}";
            return Task.FromResult(Encoding.UTF8.GetBytes(payload));
        });
        await using var controlServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)4005, packet.ApiId);
            using var request = JsonDocument.Parse(packet.Payload);
            Assert.Equal(
                "MesControlAgv.Adapter",
                request.RootElement.GetProperty("nick_name").GetString());
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
        });
        using var client = new TcpAgvClient(
            CreateControlOptions(statusServer.Port, controlServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.EnsureControlWithResultAsync(cancellation.Token);

        Assert.True(result.AcquiredByThisCall);
        Assert.Equal([1060, 1060], statusServer.ApiIds);
        Assert.Equal([4005], controlServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Ensure_control_does_not_guess_release_when_4005_result_is_unknown_even_if_1060_confirms_adapter()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ownershipQuery = 0;
        await using var statusServer = new TcpApiTestServer(2, packet =>
        {
            Assert.Equal((ushort)1060, packet.ApiId);
            var payload = Interlocked.Increment(ref ownershipQuery) switch
            {
                1 => "{\"ret_code\":0,\"locked\":false}",
                2 => "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}",
                _ => throw new InvalidOperationException("Unexpected ownership query.")
            };
            return Task.FromResult(Encoding.UTF8.GetBytes(payload));
        });
        await using var controlServer = new TcpApiTestServer(
            1,
            packet => packet.ApiId switch
            {
                4005 => Task.FromResult(Array.Empty<byte>()),
                _ => throw new InvalidOperationException($"Unexpected control API {packet.ApiId}.")
            },
            (packet, _) => packet.ApiId == 4005);
        using var client = new TcpAgvClient(
            CreateControlOptions(statusServer.Port, controlServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var exception = await Assert.ThrowsAsync<EndOfStreamException>(
            () => client.EnsureControlWithResultAsync(cancellation.Token));

        Assert.Equal("AGV closed the TCP connection.", exception.Message);
        Assert.Equal([1060, 1060], statusServer.ApiIds);
        Assert.Equal([4005], controlServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Release_control_does_not_treat_case_variant_owner_as_adapter()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1060, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"mescontrolagv.adapter\"}"));
        });
        await using var controlServer = new TcpApiTestServer(0, _ =>
            throw new InvalidOperationException("4006 must not be sent for a case-variant owner."));
        using var client = new TcpAgvClient(
            CreateControlOptions(statusServer.Port, controlServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var released = await client.ReleaseControlAsync(cancellation.Token);

        Assert.False(released);
        Assert.Equal([1060], statusServer.ApiIds);
        Assert.Empty(controlServer.ApiIds);
        await statusServer.Completion;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ensure_control_does_not_release_when_4005_outcome_cannot_be_confirmed_as_adapter(
        bool reconciliationFails)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ownershipQuery = 0;
        await using var statusServer = new TcpApiTestServer(
            2,
            packet =>
            {
                Assert.Equal((ushort)1060, packet.ApiId);
                var payload = Interlocked.Increment(ref ownershipQuery) == 1
                    ? "{\"ret_code\":0,\"locked\":false}"
                    : "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"robot-test-software\"}";
                return Task.FromResult(Encoding.UTF8.GetBytes(payload));
            },
            (_, requestIndex) => reconciliationFails && requestIndex == 1);
        await using var controlServer = new TcpApiTestServer(
            1,
            packet =>
            {
                Assert.Equal((ushort)4005, packet.ApiId);
                return Task.FromResult(Array.Empty<byte>());
            },
            (_, _) => true);
        using var client = new TcpAgvClient(
            CreateControlOptions(statusServer.Port, controlServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var exception = await Assert.ThrowsAsync<EndOfStreamException>(
            () => client.EnsureControlWithResultAsync(cancellation.Token));

        Assert.Equal("AGV closed the TCP connection.", exception.Message);
        Assert.Equal([1060, 1060], statusServer.ApiIds);
        Assert.Equal([4005], controlServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Release_control_sends_one_empty_4006_on_control_port_and_confirms_with_1060()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var observed = new List<ushort>();
        await using var statusServer = new TcpApiTestServer(2, packet =>
        {
            observed.Add(packet.ApiId);
            Assert.Equal((ushort)1060, packet.ApiId);
            var payload = observed.Count == 1
                ? "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}"
                : "{\"ret_code\":0,\"locked\":false}";
            return Task.FromResult(Encoding.UTF8.GetBytes(payload));
        });
        await using var controlServer = new TcpApiTestServer(1, packet =>
        {
            observed.Add(packet.ApiId);
            Assert.Equal((ushort)4006, packet.ApiId);
            Assert.Empty(packet.Payload);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
        });
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = controlServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        var released = await client.ReleaseControlAsync(cancellation.Token);

        Assert.True(released);
        Assert.Equal([1060, 4006, 1060], observed);
        Assert.Equal([4006], controlServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Concurrent_release_control_calls_send_one_4006_and_audit_one_ordered_attempt()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var statusRequestCount = 0;
        await using var statusServer = new TcpApiTestServer(3, packet =>
        {
            Assert.Equal((ushort)1060, packet.ApiId);
            var requestNumber = Interlocked.Increment(ref statusRequestCount);
            var payload = requestNumber == 1
                ? "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}"
                : "{\"ret_code\":0,\"locked\":false}";
            return Task.FromResult(Encoding.UTF8.GetBytes(payload));
        });
        await using var controlServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)4006, packet.ApiId);
            Assert.Empty(packet.Payload);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
        });
        var logger = new RecordingLogger<TcpAgvClient>();
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = controlServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            logger);

        var first = client.ReleaseControlAsync(cancellation.Token);
        var second = client.ReleaseControlAsync(cancellation.Token);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(released => released));
        Assert.Equal(1, results.Count(released => !released));
        Assert.Equal([1060, 1060, 1060], statusServer.ApiIds);
        Assert.Equal([4006], controlServer.ApiIds);
        var releaseAudits = logger.Messages
            .Where(message => message.Contains("\"api_id\":4006", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, releaseAudits.Length);
        Assert.Contains("request audit", releaseAudits[0], StringComparison.Ordinal);
        Assert.Contains("response audit", releaseAudits[1], StringComparison.Ordinal);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Cancelled_concurrent_release_does_not_send_an_extra_4006()
    {
        using var firstCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var waitingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ownershipReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOwnershipResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var statusServer = new TcpApiTestServer(2, async packet =>
        {
            Assert.Equal((ushort)1060, packet.ApiId);
            if (!ownershipReadStarted.Task.IsCompleted)
            {
                ownershipReadStarted.TrySetResult(true);
                await allowOwnershipResponse.Task;
                return Encoding.UTF8.GetBytes(
                    "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}");
            }

            return Encoding.UTF8.GetBytes("{\"ret_code\":0,\"locked\":false}");
        });
        await using var controlServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)4006, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
        });
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = controlServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        var first = client.ReleaseControlAsync(firstCancellation.Token);
        await ownershipReadStarted.Task;
        var cancelledWaiter = client.ReleaseControlAsync(waitingCancellation.Token);
        waitingCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);
        allowOwnershipResponse.TrySetResult(true);
        Assert.True(await first);
        Assert.Equal([4006], controlServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Release_control_fails_explicitly_when_4006_returns_nonzero()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(1, _ => Task.FromResult(Encoding.UTF8.GetBytes(
            "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}")));
        await using var controlServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)4006, packet.ApiId);
            Assert.Empty(packet.Payload);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":12345,\"err_msg\":\"release denied\"}"));
        });
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = controlServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        var exception = await Assert.ThrowsAsync<AgvApiException>(
            () => client.ReleaseControlAsync(cancellation.Token));

        Assert.Equal(4006, exception.ApiId);
        Assert.Equal(12345, exception.ErrorCode);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Release_control_fails_explicitly_when_ownership_remains_adapter()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var observed = new List<ushort>();
        await using var statusServer = new TcpApiTestServer(2, packet =>
        {
            observed.Add(packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}"));
        });
        await using var controlServer = new TcpApiTestServer(1, packet =>
        {
            observed.Add(packet.ApiId);
            Assert.Equal((ushort)4006, packet.ApiId);
            Assert.Empty(packet.Payload);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
        });
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = controlServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        await Assert.ThrowsAsync<ControlReleaseUnconfirmedException>(
            () => client.ReleaseControlAsync(cancellation.Token));

        Assert.Equal([1060, 4006, 1060], observed);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Client_reads_controller_map_evidence_from_documented_read_apis()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(3, packet =>
        {
            if (packet.ApiId == 1302)
            {
                using var request = JsonDocument.Parse(packet.Payload);
                Assert.Equal(
                    ["acceptance-map.smap"],
                    request.RootElement.GetProperty("map_names")
                        .EnumerateArray()
                        .Select(item => item.GetString())
                        .ToArray());
            }

            var payload = packet.ApiId switch
            {
                1300 => "{\"ret_code\":0,\"current_map\":\"acceptance-map\",\"maps\":[\"acceptance-map.smap\"]}",
                1301 => "{\"ret_code\":0,\"stations\":[{\"id\":\"LM1\"},{\"id\":\"LM2\"}]}",
                1302 => "{\"ret_code\":0,\"map_info\":[{\"name\":\"acceptance-map.smap\",\"md5\":\"abcdef0123456789abcdef0123456789\"}]}",
                _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
            };
            return Task.FromResult(Encoding.UTF8.GetBytes(payload));
        });
        await using var controlServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)4011, packet.ApiId);
            using var request = JsonDocument.Parse(packet.Payload);
            Assert.Equal("acceptance-map", request.RootElement.GetProperty("map_name").GetString());
            return Task.FromResult(Encoding.UTF8.GetBytes("""
            {
              "header": {
                "mapType": "smap",
                "mapName": "acceptance-map",
                "minPos": { "x": 0, "y": 0 },
                "maxPos": { "x": 10, "y": 10 },
                "resolution": 0.05,
                "version": "1.0"
              },
              "advancedPointList": [
                { "instanceName": "LM1", "pos": { "x": 1, "y": 1 } },
                { "instanceName": "LM2", "pos": { "x": 2, "y": 2 } }
              ],
              "advancedCurveList": [
                {
                  "instanceName": "edge-1",
                  "startPos": { "instanceName": "LM1", "pos": { "x": 1, "y": 1 } },
                  "endPos": { "instanceName": "LM2", "pos": { "x": 2, "y": 2 } },
                  "controlPos1": { "x": 1, "y": 1 },
                  "controlPos2": { "x": 2, "y": 2 },
                  "property": []
                }
              ]
            }
            """));
        });
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = controlServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance,
            AdapterRunMode.ReadOnlyPreflight);

        var evidence = await client.GetControllerMapEvidenceAsync(cancellation.Token);

        Assert.NotNull(evidence);
        Assert.True(evidence.IsControllerAuthoritative);
        Assert.Equal("vendor-tcp:1300,1301,1302,4011", evidence.Source);
        Assert.Equal("acceptance-map", evidence.MapName);
        Assert.Equal("1.0", evidence.Version);
        Assert.Equal("abcdef0123456789abcdef0123456789", evidence.Md5);
        Assert.Equal(["LM1", "LM2"], evidence.StationIds);
        Assert.Equal([new ControllerDirectedEdgeResponse("LM1", "LM2")], evidence.DirectedEdges);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Client_fails_closed_when_controller_map_evidence_read_is_interrupted()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1300, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"current_map\":\"acceptance-map\"}"));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, statusServer.Port),
            NullLogger<TcpAgvClient>.Instance,
            AdapterRunMode.ReadOnlyPreflight);

        var evidence = await client.GetControllerMapEvidenceAsync(cancellation.Token);

        Assert.Null(evidence);
        Assert.Equal([1300], statusServer.ApiIds);
        await statusServer.Completion;
    }

    [Fact]
    public async Task Client_retries_1302_with_smap_extension_after_vendor_map_not_found()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var md5Queries = 0;
        await using var statusServer = new TcpApiTestServer(4, packet =>
        {
            if (packet.ApiId == 1300)
            {
                return Task.FromResult(Encoding.UTF8.GetBytes(
                    "{\"ret_code\":0,\"current_map\":\"acceptance-map\",\"maps\":[]}"));
            }
            if (packet.ApiId == 1301)
            {
                return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0,\"stations\":[]}"));
            }

            Assert.Equal((ushort)1302, packet.ApiId);
            using var request = JsonDocument.Parse(packet.Payload);
            var mapName = request.RootElement.GetProperty("map_names")[0].GetString();
            md5Queries++;
            return Task.FromResult(Encoding.UTF8.GetBytes(md5Queries == 1
                ? "{\"ret_code\":40051,\"err_msg\":\"no this map file\"}"
                : $"{{\"ret_code\":0,\"map_info\":[{{\"name\":{JsonSerializer.Serialize(mapName)},\"md5\":\"abcdef0123456789abcdef0123456789\"}}]}}"));
        });
        await using var controlServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)4011, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes("""
            {
              "header": {
                "mapType": "smap",
                "mapName": "acceptance-map",
                "minPos": { "x": 0, "y": 0 },
                "maxPos": { "x": 1, "y": 1 },
                "resolution": 0.05,
                "version": "1.0"
              }
            }
            """));
        });
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = statusServer.Port,
                ControlPort = controlServer.Port,
                EnablePush = false,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance,
            AdapterRunMode.ReadOnlyPreflight);

        var evidence = await client.GetControllerMapEvidenceAsync(cancellation.Token);

        Assert.NotNull(evidence);
        Assert.True(evidence.IsControllerAuthoritative);
        Assert.Equal("abcdef0123456789abcdef0123456789", evidence.Md5);
        Assert.Equal([1300, 1301, 1302, 1302], statusServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, controlServer.Completion);
    }

    [Fact]
    public async Task Client_queries_control_and_status_before_sending_3066_route()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(7, HandleStatusAsync);
        await using var commandServer = new TcpApiTestServer(1, HandleCommandAsync);
        var options = Options.Create(new TcpAgvOptions
        {
            Host = "127.0.0.1",
            StatusPort = statusServer.Port,
            CommandPort = commandServer.Port,
            ControlPort = statusServer.Port,
            EnablePush = false,
            RequestTimeoutMs = 1000,
            ConnectTimeoutMs = 1000
        });
        using var client = new TcpAgvClient(options, NullLogger<TcpAgvClient>.Instance);

        await client.EnsureControlAsync(cancellation.Token);
        var snapshot = await client.GetSnapshotAsync(cancellation.Token);
        var taskId = Guid.NewGuid();
        var response = await client.NavigateAsync(taskId, "SAMPLE_01", "ST_PREP_01", cancellation.Token);

        Assert.True(snapshot.Online);
        Assert.Equal("adapter", snapshot.ControlOwner);
        Assert.Equal("unknown", response.State);
        Assert.Equal("dispatch_not_confirmed_by_1110", response.LastError);
        Assert.Equal(taskId.ToString("N"), commandServer.Requests.Single().TaskId);
        Assert.Equal(commandServer.Requests.Single().TaskId, response.DeviceTaskId);
        Assert.Equal("SAMPLE_01", commandServer.Requests.Single().SourceStationId);
        Assert.Equal("ST_PREP_01", commandServer.Requests.Single().TargetStationId);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_reads_station_from_wrapped_vendor_task_status_package()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(2, packet =>
        {
            var payload = packet.ApiId switch
            {
                1060 => "{\"locked\":false}",
                1110 => "{\"ret_code\":0,\"task_status_package\":{\"closest_target\":\"LM1\",\"task_status_list\":[{\"status\":4,\"task_id\":\"\"}]}}",
                _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
            };
            return Task.FromResult(Encoding.UTF8.GetBytes(payload));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, statusServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var snapshot = await client.GetSnapshotAsync(cancellation.Token);

        Assert.True(snapshot.Online);
        Assert.Equal("LM1", snapshot.CurrentStationId);
        Assert.Null(snapshot.CurrentTaskId);
        await statusServer.Completion;
    }

    [Fact]
    public async Task Client_sends_the_complete_path_as_one_deterministic_3066_batch()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(5, HandleStatusAsync);
        await using var commandServer = new TcpApiTestServer(1, HandleCommandAsync);
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);
        var taskId = Guid.NewGuid();
        string[] path = ["LM5", "LM4", "LM1"];

        var first = await client.NavigateAsync(taskId, "LM5", "LM1", path, cancellation.Token);
        var second = await client.NavigateAsync(taskId, "LM5", "LM1", path, cancellation.Token);

        Assert.Equal(taskId, first.TaskId);
        Assert.Equal(path, first.Path);
        var batch = Assert.Single(commandServer.Batches);
        Assert.Collection(batch,
            segment =>
            {
                Assert.Equal(taskId.ToString("N"), segment.TaskId);
                Assert.Equal(("LM5", "LM4"), (segment.SourceStationId, segment.TargetStationId));
            },
            segment =>
            {
                Assert.NotEqual(taskId.ToString("N"), segment.TaskId);
                Assert.Equal(("LM4", "LM1"), (segment.SourceStationId, segment.TargetStationId));
            });
        Assert.All(
            batch,
            segment => Assert.Equal(0.3, segment.MaximumSpeedMetersPerSecond));
        Assert.Equal(taskId.ToString("N"), first.DeviceTaskId);
        Assert.Equal(first.DeviceTaskId, second.DeviceTaskId);
        Assert.Equal("unknown", first.State);
        Assert.Equal("dispatch_not_confirmed_by_1110", first.LastError);
        Assert.Equal("unknown", second.State);
        Assert.Equal("dispatch_not_confirmed_by_1110", second.LastError);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_sends_3066_once_when_initial_1110_is_not_found_and_audits_the_unconfirmed_result()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(5, packet =>
        {
            var payload = packet.ApiId switch
            {
                1060 => "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}",
                1101 => "{\"ret_code\":0,\"mode\":1,\"reloc_status\":1,\"confidence\":1.0,\"emergency\":false,\"blocked\":false,\"fatals\":[],\"errors\":[],\"fork_auto_flag\":true}",
                1110 => JsonSerializer.Serialize(new
                {
                    ret_code = 0,
                    task_status_list = ReadRequestedTaskIds(packet)
                        .Select(taskId => new { task_id = taskId, status = 404 })
                }),
                _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
            };
            return Task.FromResult(Encoding.UTF8.GetBytes(payload));
        });
        await using var commandServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)3066, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"err_msg\":\"accepted-for-processing\"}"));
        });
        var logger = new RecordingLogger<TcpAgvClient>();
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            logger);
        var taskId = Guid.NewGuid();

        var first = await client.NavigateAsync(taskId, "LM1", "LM2", cancellation.Token);
        var duplicate = await client.NavigateAsync(taskId, "LM1", "LM2", cancellation.Token);

        Assert.Equal("unknown", first.State);
        Assert.Equal("dispatch_not_confirmed_by_1110", first.LastError);
        Assert.Equal("unknown", duplicate.State);
        Assert.Equal("dispatch_not_confirmed_by_1110", duplicate.LastError);
        Assert.Single(commandServer.Batches);
        Assert.Equal([3066], commandServer.ApiIds);
        Assert.Contains(logger.Messages, message =>
            message.Contains("mutation request audit", StringComparison.Ordinal)
            && message.Contains("\"api_id\":3066", StringComparison.Ordinal)
            && message.Contains("\"max_speed\":0.3", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("mutation response audit", StringComparison.Ordinal)
            && message.Contains("\"ret_code\":0", StringComparison.Ordinal)
            && message.Contains("\"err_msg\":\"accepted-for-processing\"", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message =>
            message.Contains("127.0.0.1", StringComparison.Ordinal));
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_aggregates_all_segment_statuses_under_the_parent_task()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskStatusQueries = 0;
        await using var statusServer = new TcpApiTestServer(5, packet =>
        {
            if (packet.ApiId == 1060)
            {
                return Task.FromResult(Encoding.UTF8.GetBytes("{\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}"));
            }
            if (packet.ApiId == 1101) return HandleStatusAsync(packet);
            Assert.Equal((ushort)1110, packet.ApiId);
            if (Interlocked.Increment(ref taskStatusQueries) == 1)
            {
                return Task.FromResult(EmptyTaskStatusResponse());
            }

            var taskIds = ReadRequestedTaskIds(packet);
            return Task.FromResult(TaskStatusResponse(
                (taskIds[0], 4, "LM4"),
                (taskIds[1], 2, "LM1")));
        });
        await using var commandServer = new TcpApiTestServer(1, HandleCommandAsync);
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);
        var taskId = Guid.NewGuid();
        string[] path = ["LM5", "LM4", "LM1"];

        await client.NavigateAsync(taskId, "LM5", "LM1", path, cancellation.Token);
        var result = await client.GetTaskAsync(taskId, path, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal(taskId, result.TaskId);
        Assert.Equal("moving", result.State);
        Assert.Equal("LM1", result.TargetStationId);
        Assert.Equal(path, result.Path);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Theory]
    [InlineData(6, "cancelled", null)]
    [InlineData(2, "unknown", "cancel_not_confirmed_by_1110")]
    public async Task Client_confirms_batch_cancellation_only_when_every_segment_is_terminal(
        int finalSegmentStatus,
        string expectedState,
        string? expectedError)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(finalSegmentStatus == 6 ? 1 : 2, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            var taskIds = ReadRequestedTaskIds(packet);
            return Task.FromResult(TaskStatusResponse(
                (taskIds[0], 4, "LM4"),
                (taskIds[1], finalSegmentStatus, "LM1")));
        });
        await using var commandServer = new TcpApiTestServer(finalSegmentStatus == 6 ? 0 : 1, packet =>
        {
            Assert.Equal((ushort)3067, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);
        var taskId = Guid.NewGuid();
        string[] path = ["LM5", "LM4", "LM1"];

        var result = await client.CancelAsync(taskId, path, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal(taskId, result.TaskId);
        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedError, result.LastError);
        Assert.Equal(path, result.Path);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_blocks_3066_when_realtime_safety_status_is_not_ready()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(6, HandleUnsafeStatusAsync);
        await using var commandServer = new TcpApiTestServer(0, HandleCommandAsync);
        var options = Options.Create(new TcpAgvOptions
        {
            Host = "127.0.0.1",
            StatusPort = statusServer.Port,
            CommandPort = commandServer.Port,
            ControlPort = statusServer.Port,
            EnablePush = false,
            RequestTimeoutMs = 1000,
            ConnectTimeoutMs = 1000
        });
        using var client = new TcpAgvClient(options, NullLogger<TcpAgvClient>.Instance);

        await client.EnsureControlAsync(cancellation.Token);
        _ = await client.GetSnapshotAsync(cancellation.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NavigateAsync(
            Guid.NewGuid(), "SAMPLE_01", "ST_PREP_01", cancellation.Token));
    }

    [Fact]
    public async Task Physical_mode_blocks_3066_when_realtime_safety_status_is_incomplete()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(3, packet => packet.ApiId switch
        {
            1110 => Task.FromResult(EmptyTaskStatusResponse()),
            1101 => Task.FromResult(Encoding.UTF8.GetBytes("{\"emergency\":false}")),
            1021 => Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}")),
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        });
        await using var commandServer = new TcpApiTestServer(0, HandleCommandAsync);
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = commandServer.Port,
                ControlPort = statusServer.Port,
                EnablePush = false,
                RequireCompleteSafetyStatus = true,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NavigateAsync(
            Guid.NewGuid(), "SAMPLE_01", "ST_PREP_01", cancellation.Token));

        await statusServer.Completion;
    }

    [Fact]
    public async Task Physical_mode_uses_1021_localization_for_the_final_pre_3066_safety_gate()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(5, packet =>
        {
            var payload = packet.ApiId switch
            {
                1110 => JsonSerializer.Serialize(new
                {
                    ret_code = 0,
                    task_status_list = ReadRequestedTaskIds(packet)
                        .Select(id => new { task_id = id, status = 404 })
                }),
                1101 => "{\"ret_code\":0,\"emergency\":false,\"blocked\":false,\"fatals\":[],\"errors\":[],\"confidence\":0.9582}",
                1021 => "{\"ret_code\":0,\"reloc_status\":1}",
                1060 => "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}",
                _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
            };
            return Task.FromResult(Encoding.UTF8.GetBytes(payload));
        });
        await using var commandServer = new TcpApiTestServer(1, HandleCommandAsync);
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = commandServer.Port,
                ControlPort = statusServer.Port,
                EnablePush = false,
                RequireCompleteSafetyStatus = true,
                RequireAutomaticMode = false,
                MinimumConfidence = 0.95,
                MaximumNavigationSpeedMetersPerSecond = 0.3,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.NavigateAsync(taskId, "LM1", "LM2", cancellation.Token);

        Assert.True(client.MayHaveWrittenNavigation(taskId));
        Assert.Equal("unknown", result.State);
        Assert.Equal([1110, 1101, 1021, 1060, 1110], statusServer.ApiIds);
        Assert.Equal([3066], commandServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Navigation_connection_failure_does_not_mark_3066_as_possibly_written()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(3, packet => packet.ApiId switch
        {
            1110 => Task.FromResult(EmptyTaskStatusResponse()),
            1101 => HandleStatusAsync(packet),
            1060 => Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}")),
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        });
        var closedCommandPort = ReserveClosedLoopbackPort();
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, closedCommandPort),
            NullLogger<TcpAgvClient>.Instance);

        var exception = await Record.ExceptionAsync(() =>
            client.NavigateAsync(taskId, "LM1", "LM2", cancellation.Token));

        Assert.True(exception is SocketException or TimeoutException);

        Assert.False(client.MayHaveWrittenNavigation(taskId));
        Assert.Equal([1110, 1101, 1060], statusServer.ApiIds);
        await statusServer.Completion;
    }

    [Fact]
    public async Task Navigation_response_failure_marks_3066_as_possibly_written()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(3, packet => packet.ApiId switch
        {
            1110 => Task.FromResult(EmptyTaskStatusResponse()),
            1101 => HandleStatusAsync(packet),
            1060 => Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}")),
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        });
        await using var commandServer = new TcpApiTestServer(
            1,
            packet =>
            {
                Assert.Equal((ushort)3066, packet.ApiId);
                return Task.FromResult(Array.Empty<byte>());
            },
            (_, _) => true);
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        await Assert.ThrowsAsync<EndOfStreamException>(() => client.NavigateAsync(
            taskId, "LM1", "LM2", cancellation.Token));

        Assert.True(client.MayHaveWrittenNavigation(taskId));
        Assert.Equal([3066], commandServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Theory]
    [InlineData("{\"ret_code\":0,\"reloc_status\":0}")]
    [InlineData("{\"ret_code\":32001,\"reloc_status\":1}")]
    [InlineData("{\"ret_code\":0}")]
    public async Task Physical_mode_blocks_3066_when_authoritative_1021_is_not_successful(
        string localizationPayload)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(3, packet => packet.ApiId switch
        {
            1110 => Task.FromResult(EmptyTaskStatusResponse()),
            1101 => Task.FromResult(Encoding.UTF8.GetBytes(
                "{\"ret_code\":0,\"reloc_status\":1,\"confidence\":0.9582,\"emergency\":false,\"blocked\":false,\"fatals\":[],\"errors\":[]}")),
            1021 => Task.FromResult(Encoding.UTF8.GetBytes(localizationPayload)),
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        });
        await using var commandServer = new TcpApiTestServer(0, _ =>
            throw new InvalidOperationException("3066 must not be sent."));
        using var client = new TcpAgvClient(
            CreatePhysicalOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.NavigateAsync(
            Guid.NewGuid(), "LM1", "LM2", cancellation.Token));

        Assert.Equal([1110, 1101, 1021], statusServer.ApiIds);
        Assert.Empty(commandServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task Physical_mode_blocks_3066_for_non_finite_string_confidence(string confidence)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(3, packet => packet.ApiId switch
        {
            1110 => Task.FromResult(EmptyTaskStatusResponse()),
            1101 => Task.FromResult(Encoding.UTF8.GetBytes(
                $"{{\"ret_code\":0,\"confidence\":\"{confidence}\",\"emergency\":false,\"blocked\":false,\"fatals\":[],\"errors\":[]}}")),
            1021 => Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0,\"reloc_status\":1}")),
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        });
        await using var commandServer = new TcpApiTestServer(0, _ =>
            throw new InvalidOperationException("3066 must not be sent."));
        using var client = new TcpAgvClient(
            CreatePhysicalOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.NavigateAsync(
            Guid.NewGuid(), "LM1", "LM2", cancellation.Token));

        Assert.Contains("safety status is incomplete", exception.Message, StringComparison.Ordinal);
        Assert.Equal([1110, 1101, 1021], statusServer.ApiIds);
        Assert.Empty(commandServer.ApiIds);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Theory]
    [InlineData(0, "unknown")]
    [InlineData(1, "accepted")]
    [InlineData(2, "moving")]
    [InlineData(6, "cancelled")]
    public async Task Client_maps_device_task_status_conservatively(int deviceStatus, string expectedState)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            return Task.FromResult(TaskStatusResponse(taskId, deviceStatus));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, statusServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.GetTaskAsync(taskId, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal(expectedState, result.State);
        await statusServer.Completion;
    }

    [Fact]
    public async Task Client_reports_cancelled_only_after_1110_confirms_status_6()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            return Task.FromResult(TaskStatusResponse(taskId, 6));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, statusServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.CancelAsync(taskId, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal("cancelled", result.State);
        Assert.Equal("ST_PREP_01", result.TargetStationId);
        Assert.Null(result.LastError);
        Assert.False(client.MayHaveWrittenCancellation(taskId));
        await statusServer.Completion;
    }

    [Fact]
    public async Task Client_returns_unknown_when_1110_does_not_confirm_cancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(2, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            return Task.FromResult(TaskStatusResponse(taskId, 2));
        });
        await using var commandServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)3067, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.CancelAsync(taskId, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal("unknown", result.State);
        Assert.Equal("cancel_not_confirmed_by_1110", result.LastError);
        Assert.True(client.MayHaveWrittenCancellation(taskId));
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_does_not_mark_cancellation_written_when_3067_connection_fails_before_write()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            return Task.FromResult(TaskStatusResponse(taskId, 2));
        });
        var closedCommandPort = ReserveClosedLoopbackPort();
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, closedCommandPort),
            NullLogger<TcpAgvClient>.Instance);

        var exception = await Record.ExceptionAsync(() => client.CancelAsync(taskId, cancellation.Token));

        Assert.True(exception is SocketException or TimeoutException);

        Assert.False(client.MayHaveWrittenCancellation(taskId));
        await statusServer.Completion;
    }

    [Fact]
    public async Task Client_marks_cancellation_written_when_3067_response_transport_fails()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            return Task.FromResult(TaskStatusResponse(taskId, 2));
        });
        await using var commandServer = new TcpApiTestServer(
            1,
            packet =>
            {
                Assert.Equal((ushort)3067, packet.ApiId);
                return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
            },
            closeWithoutResponse: (_, _) => true);
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        await Assert.ThrowsAnyAsync<IOException>(() => client.CancelAsync(taskId, cancellation.Token));

        Assert.True(client.MayHaveWrittenCancellation(taskId));
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_returns_unknown_when_1110_does_not_find_cancelled_task()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0,\"task_status_list\":[]}"));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, statusServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.CancelAsync(taskId, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal("unknown", result.State);
        Assert.Equal("cancel_not_confirmed_by_1110", result.LastError);
        await statusServer.Completion;
    }

    private static IOptions<TcpAgvOptions> CreateOptions(int statusPort, int commandPort) => Options.Create(new TcpAgvOptions
    {
        Host = "127.0.0.1",
        StatusPort = statusPort,
        CommandPort = commandPort,
        ControlPort = statusPort,
        EnablePush = false,
        MaximumNavigationSpeedMetersPerSecond = 0.3,
        RequestTimeoutMs = 1000,
        ConnectTimeoutMs = 1000
    });

    private static IOptions<TcpAgvOptions> CreateControlOptions(int statusPort, int controlPort) =>
        Options.Create(new TcpAgvOptions
        {
            Host = "127.0.0.1",
            StatusPort = statusPort,
            CommandPort = statusPort,
            ControlPort = controlPort,
            EnablePush = false,
            AcquireControl = true,
            RequestTimeoutMs = 500,
            ConnectTimeoutMs = 500
        });

    private static IOptions<TcpAgvOptions> CreatePhysicalOptions(int statusPort, int commandPort) =>
        Options.Create(new TcpAgvOptions
        {
            Host = "127.0.0.1",
            StatusPort = statusPort,
            CommandPort = commandPort,
            ControlPort = statusPort,
            EnablePush = false,
            RequireCompleteSafetyStatus = true,
            RequireAutomaticMode = false,
            MinimumConfidence = 0.95,
            MaximumNavigationSpeedMetersPerSecond = 0.3,
            RequestTimeoutMs = 1000,
            ConnectTimeoutMs = 1000
        });

    private static int ReserveClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] TaskStatusResponse(Guid taskId, int status) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            ret_code = 0,
            task_status_list = new[] { new { task_id = taskId.ToString("N"), status, target_name = "ST_PREP_01" } }
        });

    private static byte[] EmptyTaskStatusResponse() =>
        Encoding.UTF8.GetBytes("{\"ret_code\":0,\"task_status_list\":[]}");

    private static byte[] TaskStatusResponse(params (string TaskId, int Status, string Target)[] statuses) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            ret_code = 0,
            task_status_list = statuses.Select(status => new
            {
                task_id = status.TaskId,
                status = status.Status,
                target_name = status.Target
            })
        });

    private static string[] ReadRequestedTaskIds(AgvTcpPacket packet)
    {
        using var document = JsonDocument.Parse(packet.Payload);
        return document.RootElement.GetProperty("task_ids")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static Task<byte[]> HandleStatusAsync(AgvTcpPacket packet)
    {
        if (packet.ApiId == 1101)
        {
            using var document = JsonDocument.Parse(packet.Payload);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.True(document.RootElement.TryGetProperty("return_laser", out var returnLaser));
            Assert.False(returnLaser.GetBoolean());
        }

        var payload = packet.ApiId switch
        {
            1060 => "{\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}",
            1110 => "{\"task_status_list\":[]}",
            1101 => "{\"mode\":1,\"reloc_status\":1,\"confidence\":1.0,\"emergency\":false,\"fatals\":[],\"errors\":[],\"fork_auto_flag\":true}",
            1021 => "{\"ret_code\":0,\"reloc_status\":1}",
            1000 => "{\"ret_code\":0,\"model\":\"W500-SZ\",\"version\":\"v3.4.8.0011\"}",
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        };
        return Task.FromResult(Encoding.UTF8.GetBytes(payload));
    }

    private static Task<byte[]> HandleCommandAsync(AgvTcpPacket packet)
    {
        Assert.Equal((ushort)3066, packet.ApiId);
        return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
    }

    private static Task<byte[]> HandleUnsafeStatusAsync(AgvTcpPacket packet)
    {
        var payload = packet.ApiId switch
        {
            1060 => "{\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}",
            1110 => "{\"task_status_list\":[]}",
            1101 => "{\"reloc_status\":1,\"emergency\":true,\"fatals\":[],\"errors\":[]}",
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        };
        return Task.FromResult(Encoding.UTF8.GetBytes(payload));
    }
}

internal sealed class TcpApiTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly int _expectedRequests;
    private readonly Func<AgvTcpPacket, Task<byte[]>> _handler;
    private readonly Func<AgvTcpPacket, int, bool> _closeWithoutResponse;
    private readonly List<RouteRequest> _requests = [];
    private readonly List<IReadOnlyList<RouteRequest>> _batches = [];
    private readonly List<ushort> _apiIds = [];

    public TcpApiTestServer(
        int expectedRequests,
        Func<AgvTcpPacket, Task<byte[]>> handler,
        Func<AgvTcpPacket, int, bool>? closeWithoutResponse = null)
    {
        _expectedRequests = expectedRequests;
        _handler = handler;
        _closeWithoutResponse = closeWithoutResponse ?? ((_, _) => false);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Completion = expectedRequests == 0 ? Task.CompletedTask : RunAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public IReadOnlyList<RouteRequest> Requests => _requests;
    public IReadOnlyList<IReadOnlyList<RouteRequest>> Batches => _batches;
    public IReadOnlyList<ushort> ApiIds => _apiIds;
    public bool HasPendingConnection => _listener.Pending();
    public Task Completion { get; }

    private async Task RunAsync()
    {
        try
        {
            var requestIndex = 0;
            while (requestIndex < _expectedRequests)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                while (requestIndex < _expectedRequests)
                {
                    var packet = await AgvTcpProtocol.ReadPacketAsync(stream, 1024 * 1024, CancellationToken.None);
                    _apiIds.Add(packet.ApiId);
                    if (packet.ApiId == 3066) RecordNavigationRequest(packet);

                    var currentRequestIndex = requestIndex++;
                    var response = await _handler(packet);
                    if (_closeWithoutResponse(packet, currentRequestIndex)) break;

                    var responsePacket = AgvTcpProtocol.CreatePacket((ushort)(packet.ApiId + 10000), response);
                    await stream.WriteAsync(responsePacket);
                    await stream.FlushAsync();
                }
            }
        }
        finally
        {
            _listener.Stop();
        }
    }

    private void RecordNavigationRequest(AgvTcpPacket packet)
    {
        using var document = JsonDocument.Parse(packet.Payload);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        var rootProperty = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("move_task_list", rootProperty.Name);
        Assert.Equal(JsonValueKind.Array, rootProperty.Value.ValueKind);
        var batch = rootProperty.Value.EnumerateArray()
            .Select(item =>
            {
                var propertyCount = item.EnumerateObject().Count();
                Assert.Contains(propertyCount, new[] { 3, 4 });
                return new RouteRequest(
                    item.GetProperty("task_id").GetString()!,
                    item.GetProperty("source_id").GetString()!,
                    item.GetProperty("id").GetString()!,
                    item.TryGetProperty("max_speed", out var maxSpeed)
                        ? maxSpeed.GetDouble()
                        : null);
            })
            .ToArray();
        Assert.NotEmpty(batch);
        _batches.Add(batch);
        _requests.AddRange(batch);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try { await Completion; }
        catch (SocketException) { }
        catch (IOException) { }
    }
}

internal sealed record RouteRequest(
    string TaskId,
    string SourceStationId,
    string TargetStationId,
    double? MaximumSpeedMetersPerSecond = null);

internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate) return _messages.ToArray();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate) _messages.Add(formatter(state, exception));
    }
}
