using System.Text;
using MesControlAgv.Adapter.Modules.SampleWorkstation;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Adapter.Tests;

public sealed class SampleWorkstationOptionsTests
{
    [Fact]
    public void Omitted_unsupported_list_keeps_field_version_defaults()
    {
        var options = Bind("{}");
        Assert.Equal(7, options.UnsupportedProtocolOperations.Length);
        Assert.Contains(SampleWorkstationProtocolOperation.WorkflowList, options.UnsupportedProtocolOperations);
        Assert.False(options.ControlEnabled);
    }

    [Fact]
    public void Explicit_unsupported_list_replaces_defaults_instead_of_appending()
    {
        var options = Bind("""{"UnsupportedProtocolOperations":["WorkflowDetails"]}""");
        Assert.Equal([SampleWorkstationProtocolOperation.WorkflowDetails], options.UnsupportedProtocolOperations);
    }

    [Fact]
    public void Explicit_empty_list_clears_defaults_after_vendor_upgrade()
    {
        var options = Bind("""{"UnsupportedProtocolOperations":[]}""");
        Assert.Empty(options.UnsupportedProtocolOperations);
    }

    [Theory]
    [InlineData("UnknownOperation")]
    [InlineData("999")]
    public void Invalid_unsupported_operation_is_rejected(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Bind($$"""{"UnsupportedProtocolOperations":["{{value}}"]}"""));
    }

    private static SampleWorkstationOptions Bind(string options)
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("{\"Devices\":{\"SampleWorkstation\":" + options + "}}"));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        return SampleWorkstationOptions.BindAndValidate(configuration);
    }
}
