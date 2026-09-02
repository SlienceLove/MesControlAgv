using System.Globalization;
using System.Windows.Media;
using MesControlAgv.Wpf.Converters;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class StartupConfigurationInspectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MesControlAgv.StartupConfigurationInspectorTests",
        Guid.NewGuid().ToString("N"));

    public StartupConfigurationInspectorTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Simulator_defaults_are_startable_and_never_report_real_writes()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory
        });

        Assert.True(report.CanStart);
        Assert.Equal("simulator", report.RuntimeMode);
        Assert.True(report.ManageLocalServices);
        Assert.Contains("simulator", report.AdapterDriver, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("禁用", report.RealWriteAccess, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, item => item.Severity == StartupDiagnosticSeverity.Error);
    }

    [Fact]
    public void Physical_read_only_config_reports_vendor_driver_and_closed_write_boundary()
    {
        var configurationPath = WriteAdapterConfiguration("read-only-preflight", "vendor-tcp");

        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "physical",
            ManageLocalServices = "false",
            AdapterConfigurationPath = configurationPath
        });

        Assert.True(report.CanStart);
        Assert.Equal("vendor-tcp", report.AdapterDriver);
        Assert.Equal("read-only-preflight", report.AdapterRunMode);
        Assert.Equal("禁用（read-only-preflight）", report.RealWriteAccess);
    }

    [Fact]
    public void Physical_standard_config_warns_that_real_writes_may_be_open()
    {
        var configurationPath = WriteAdapterConfiguration("standard", "vendor-tcp");

        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "physical",
            AdapterConfigurationPath = configurationPath
        });

        Assert.True(report.CanStart);
        Assert.True(report.HasWarnings);
        Assert.StartsWith("可能开放", report.RealWriteAccess, StringComparison.Ordinal);
        Assert.Contains(report.Items, item => item.Code == "REAL_WRITE_ACCESS" && item.Severity == StartupDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Invalid_mode_url_and_local_service_flag_are_reported_without_network_access()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "danger",
            ManageLocalServices = "sometimes",
            MesBaseUrl = "ftp://controller.invalid/"
        });

        Assert.False(report.CanStart);
        Assert.True(report.HasErrors);
        Assert.Contains(report.Items, item => item.Code == "WPF_RUNTIME_MODE_INVALID");
        Assert.Contains(report.Items, item => item.Code == "WPF_MANAGE_LOCAL_SERVICES_INVALID");
        Assert.Contains(report.Items, item => item.Code == "MES_BASE_URL_INVALID");
    }

    [Fact]
    public void Adapter_device_modules_report_control_conflicts_and_unregistered_vision()
    {
        var path = Path.Combine(_directory, "adapter-modules.json");
        File.WriteAllText(path, """
        {
          "Adapter": { "RunMode": "read-only-preflight" },
          "Agv": { "Driver": "vendor-tcp" },
          "Devices": {
            "AuboArm": { "Enabled": true, "ControlEnabled": true, "Host": "controller.invalid" },
            "SampleWorkstation": {
              "Enabled": true,
              "ControlEnabled": true,
              "EquipmentNo": "",
              "BaseUrl": "http://127.0.0.1:8082/Service/"
            }
          }
        }
        """);

        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "physical",
            AdapterConfigurationPath = path
        });

        Assert.False(report.CanStart);
        Assert.Contains(report.Items, item => item.Code == "AUBO_CONTROL_BLOCKED_BY_RUN_MODE");
        Assert.Contains(report.Items, item => item.Code == "WORKSTATION_CONTROL_FORBIDDEN");
        Assert.Contains(report.Items, item => item.Code == "WORKSTATION_EQUIPMENT_REQUIRED");
        Assert.Contains(report.Items, item => item.Code == "VISION_MODULE_REGISTRATION" && item.Severity == StartupDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Aubo_simulator_is_reported_as_local_control_without_requiring_a_host()
    {
        var path = Path.Combine(_directory, "adapter-aubo-simulator.json");
        File.WriteAllText(path, """
        {
          "Adapter": { "RunMode": "standard" },
          "Agv": { "Driver": "simulator" },
          "Devices": {
            "AuboArm": {
              "Driver": "simulator",
              "Enabled": true,
              "ControlEnabled": true,
              "Host": "",
              "AllowedProgramNames": ["测试1"]
            }
          }
        }
        """);

        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false",
            AdapterConfigurationPath = path
        });

        Assert.True(report.CanStart);
        Assert.Contains(report.Items, item =>
            item.Code == "AUBO_ARM_MODULE" && item.Value == "启用（本地模拟控制）");
        Assert.DoesNotContain(report.Items, item => item.Code == "AUBO_HOST_REQUIRED");
    }

    [Fact]
    public void Physical_mode_rejects_an_aubo_simulator_driver()
    {
        var path = Path.Combine(_directory, "adapter-aubo-simulator-physical.json");
        File.WriteAllText(path, """
        {
          "Adapter": { "RunMode": "standard" },
          "Agv": { "Driver": "vendor-tcp" },
          "Devices": {
            "AuboArm": { "Driver": "simulator", "Enabled": true, "ControlEnabled": true }
          }
        }
        """);

        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "physical",
            AdapterConfigurationPath = path
        });

        Assert.False(report.CanStart);
        Assert.Contains(report.Items, item => item.Code == "AUBO_SIMULATOR_FORBIDDEN_IN_PHYSICAL");
    }

    [Fact]
    public void Disabled_instrument_gateway_is_valid_and_keeps_writes_closed()
    {
        var path = WriteInstrumentConfiguration(enabled: false, instrumentId: "CIC-D160-01", comPort: "COM4");

        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            InstrumentGatewayConfigurationPath = path
        });

        Assert.True(report.CanStart);
        Assert.Contains(report.Items, item => item.Code == "ION_CHROMATOGRAPHY_MODULE" && item.Value == "禁用");
        Assert.Contains(report.Items, item => item.Code == "INSTRUMENT_WRITE_ACCESS" && item.Value.StartsWith("禁用", StringComparison.Ordinal));
    }

    [Fact]
    public void Enabled_instrument_gateway_validates_required_serial_settings_offline()
    {
        var path = WriteInstrumentConfiguration(enabled: true, instrumentId: "", comPort: "", baudRate: 0, dataBits: 9);

        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            InstrumentGatewayConfigurationPath = path
        });

        Assert.False(report.CanStart);
        Assert.Contains(report.Items, item => item.Code == "ION_INSTRUMENT_ID_REQUIRED");
        Assert.Contains(report.Items, item => item.Code == "ION_COM_PORT_REQUIRED");
        Assert.Contains(report.Items, item => item.Code == "ION_BAUD_RATE_INVALID");
        Assert.Contains(report.Items, item => item.Code == "ION_DATA_BITS_INVALID");
    }

    [Theory]
    [InlineData("禁用（read-only-preflight）", "#12B76A")]
    [InlineData("离线已知", "#12B76A")]
    [InlineData("未变化", "#12B76A")]
    [InlineData("已变化", "#F79009")]
    [InlineData("移除", "#F04438")]
    [InlineData("当前格式", "#12B76A")]
    [InlineData("需升级", "#F79009")]
    [InlineData("无法读取", "#F04438")]
    [InlineData("清理候选（需人工确认）", "#F79009")]
    [InlineData("可能开放（仍需现场授权）", "#F79009")]
    [InlineData("配置检查失败", "#F04438")]
    public void Diagnostic_palette_distinguishes_safe_warning_and_error(string text, string expectedHex)
    {
        var brush = Assert.IsType<SolidColorBrush>(new StartupDiagnosticBrushConverter().Convert(
            text,
            typeof(SolidColorBrush),
            null,
            CultureInfo.InvariantCulture));

        Assert.Equal((Color)ColorConverter.ConvertFromString(expectedHex), brush.Color);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string WriteAdapterConfiguration(string runMode, string driver)
    {
        var path = Path.Combine(_directory, $"adapter-{runMode}.json");
        File.WriteAllText(path, $$"""
        {
          "Adapter": { "RunMode": "{{runMode}}" },
          "Agv": { "Driver": "{{driver}}" }
        }
        """);
        return path;
    }

    private string WriteInstrumentConfiguration(
        bool enabled,
        string instrumentId,
        string comPort,
        int baudRate = 115200,
        int dataBits = 8)
    {
        var path = Path.Combine(_directory, $"instrument-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
        {
          "IonChromatography": {
            "Enabled": {{enabled.ToString().ToLowerInvariant()}},
            "InstrumentId": "{{instrumentId}}",
            "ComPort": "{{comPort}}",
            "BaudRate": {{baudRate}},
            "DataBits": {{dataBits}},
            "TimeoutMs": 3000,
            "SlaveAddress": 1
          }
        }
        """);
        return path;
    }
}
