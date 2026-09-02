using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class StartupConfigurationReplayTests
{
    public static IEnumerable<object[]> AdapterCases()
    {
        yield return
        [
            "simulator",
            "simulator-adapter.json",
            "simulator",
            "standard",
            true,
            "禁用（仅模拟器）",
            Array.Empty<string>()
        ];
        yield return
        [
            "physical-read-only",
            "physical-readonly-adapter.json",
            "vendor-tcp",
            "read-only-preflight",
            true,
            "禁用（read-only-preflight）",
            Array.Empty<string>()
        ];
        yield return
        [
            "physical-standard-conflict",
            "physical-standard-conflict-adapter.json",
            "vendor-tcp",
            "standard",
            false,
            "可能开放（仍需现场授权）",
            new[] { "AUBO_HOST_REQUIRED", "WORKSTATION_CONTROL_FORBIDDEN", "WORKSTATION_EQUIPMENT_REQUIRED", "WORKSTATION_URL_INVALID" }
        ];
        yield return
        [
            "physical-vision-proposal",
            "vision-proposed-readonly.json",
            "vendor-tcp",
            "read-only-preflight",
            true,
            "禁用（read-only-preflight）",
            Array.Empty<string>()
        ];
    }

    public static IEnumerable<object[]> InstrumentCases()
    {
        yield return
        [
            "instrument-valid",
            "instrument-valid.json",
            true,
            Array.Empty<string>()
        ];
        yield return
        [
            "instrument-invalid",
            "instrument-invalid.json",
            false,
            new[] { "ION_INSTRUMENT_ID_REQUIRED", "ION_COM_PORT_REQUIRED", "ION_BAUD_RATE_INVALID", "ION_DATA_BITS_INVALID", "ION_TIMEOUT_INVALID", "ION_SLAVE_ADDRESS_INVALID" }
        ];
        yield return
        [
            "instrument-physical-readonly",
            "instrument-physical-readonly.json",
            true,
            Array.Empty<string>()
        ];
    }

    [Theory]
    [MemberData(nameof(AdapterCases))]
    public void Adapter_fixture_replay_is_deterministic(
        string name,
        string fileName,
        string expectedDriver,
        string expectedRunMode,
        bool canStart,
        string expectedWrites,
        IReadOnlyList<string> expectedCodes)
    {
        var path = Fixture(fileName);
        var input = new StartupConfigurationInput
        {
            RuntimeMode = name == "simulator" ? "simulator" : "physical",
            ManageLocalServices = "false",
            AdapterConfigurationPath = path,
            BaseDirectory = Path.GetDirectoryName(path)!
        };

        var first = StartupConfigurationInspector.Inspect(input);
        var second = StartupConfigurationInspector.Inspect(input);

        Assert.Equal(canStart, first.CanStart);
        Assert.Equal(expectedDriver, first.AdapterDriver);
        Assert.Equal(expectedRunMode, first.AdapterRunMode);
        Assert.Equal(expectedWrites, first.RealWriteAccess);
        Assert.Equal(
            first.Items.Select(item => (item.Code, item.Value, item.Severity)).ToArray(),
            second.Items.Select(item => (item.Code, item.Value, item.Severity)).ToArray());
        Assert.All(expectedCodes, code => Assert.Contains(first.Items, item => item.Code == code));
        Assert.Contains(first.Items, item => item.Code == "VISION_MODULE_REGISTRATION");
        if (name == "physical-vision-proposal")
        {
            Assert.Contains(first.Items, item =>
                item.Code == "VISION_MODULE_REGISTRATION" &&
                item.Value.Contains("配置存在但未注册", StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(InstrumentCases))]
    public void Instrument_fixture_replay_preserves_read_only_boundary(
        string name,
        string fileName,
        bool canStart,
        IReadOnlyList<string> expectedCodes)
    {
        _ = name;
        var input = new StartupConfigurationInput
        {
            InstrumentGatewayConfigurationPath = Fixture(fileName),
            BaseDirectory = Path.GetDirectoryName(Fixture(fileName))!
        };

        var report = StartupConfigurationInspector.Inspect(input);

        Assert.Equal(canStart, report.CanStart);
        Assert.Contains(report.Items, item =>
            item.Code == "INSTRUMENT_WRITE_ACCESS" &&
            item.Value.StartsWith("禁用", StringComparison.Ordinal));
        Assert.All(expectedCodes, code => Assert.Contains(report.Items, item => item.Code == code));
    }

    private static string Fixture(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MesControlAgv.sln")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository root was not found.");
        return Path.Combine(directory.FullName, "tests", "MesControlAgv.Wpf.Tests", "fixtures", "startup-diagnostics", fileName);
    }
}
