using System.Net;
using System.Net.Sockets;
using System.Text;
using MesControlAgv.Adapter.Modules.AuboArm;
using Xunit;

namespace MesControlAgv.Adapter.Tests;

public sealed class AuboArmDashboardClientTests
{
    [Fact]
    public async Task Reads_the_documented_loaded_program_name_without_sending_a_control_command()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var options = new AuboArmOptions
        {
            DeviceId = "ARM-01",
            Host = "127.0.0.1",
            DashboardPort = port,
            DashboardTimeoutMs = 2000
        };
        using var client = new AuboArmDashboardClient(options);

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var stream = server.GetStream();
            var buffer = new byte[128];
            var read = await stream.ReadAsync(buffer);
            var command = Encoding.ASCII.GetString(buffer, 0, read);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(
                "Loaded program: </root/arcs_ws/program/现场2.pro>\n"));
            return command;
        });

        var program = await client.GetLoadedProgramAsync(options.DeviceId, CancellationToken.None);
        var commandText = await serverTask;

        Assert.Equal("现场2", program);
        Assert.Equal("get loaded program\n", commandText);
    }
}
