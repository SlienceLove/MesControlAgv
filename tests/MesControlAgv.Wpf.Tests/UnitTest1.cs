using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MesControlAgv.Wpf.Converters;

namespace MesControlAgv.Wpf.Tests;

public sealed class ValueConverterTests
{
    [Theory]
    [InlineData(null, Visibility.Collapsed)]
    [InlineData("", Visibility.Collapsed)]
    [InlineData("   ", Visibility.Collapsed)]
    [InlineData("设备在线", Visibility.Visible)]
    public void String_to_visibility_converter_handles_empty_and_non_empty_values(
        string? value,
        Visibility expected)
    {
        var converter = new StringToVisibilityConverter();

        var actual = converter.Convert(value, typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void String_to_visibility_converter_ignores_accidental_reverse_binding()
    {
        var converter = new StringToVisibilityConverter();

        var actual = converter.ConvertBack(
            Visibility.Visible,
            typeof(string),
            null,
            CultureInfo.InvariantCulture);

        Assert.Same(Binding.DoNothing, actual);
    }
}
