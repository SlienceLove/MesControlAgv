using MesControlAgv.InstrumentGateway;

namespace MesControlAgv.InstrumentGateway.Tests;

public sealed class CicD160PlusOptionsTests
{
    [Fact]
    public void PhysicalAcceptance_requires_two_independent_pending_devices()
    {
        var options = new CicD160PlusOptions
        {
            Devices = new(StringComparer.OrdinalIgnoreCase)
            {
                ["CIC-D160-01"] = new() { DeviceId = "CIC-D160-01" },
                ["CIC-D160-02"] = new() { DeviceId = "CIC-D160-02" }
            }
        };
        Assert.True(options.HasValidPendingDeviceGates());

        options.Devices["CIC-D160-02"].ControlEnabled = true;
        Assert.False(options.HasValidPendingDeviceGates());
        options.Devices["CIC-D160-02"].ControlEnabled = false;
        options.Devices["CIC-D160-02"].ProtocolStatus = "ready";
        Assert.False(options.HasValidPendingDeviceGates());
    }
}
