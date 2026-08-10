using System.Text;
using MesControlAgv.Domain.Map;

namespace MesControlAgv.Domain.Tests.Map;

public sealed class SmapParserTests
{
    [Fact]
    public void Parses_header_and_defaults_missing_coordinates_to_zero()
    {
        using var json = JsonStream(
            """
            {
              "header": {
                "mapType": "2D-Map",
                "mapName": "guangzhou606",
                "minPos": { "x": -1.5, "y": -2.5 },
                "maxPos": { "x": 10.0, "y": 20.0 },
                "resolution": 0.02,
                "version": "1.0.6"
              },
              "advancedPointList": [
                {
                  "instanceName": "LM1",
                  "pos": { "y": 0.8 }
                }
              ]
            }
            """);

        var document = SmapParser.Parse(json);

        Assert.Equal("guangzhou606", document.Header.MapName);
        Assert.Equal("1.0.6", document.Header.Version);
        Assert.Equal(-1.5, document.Header.MinPos.X);
        var mark = Assert.Single(document.LocationMarks);
        Assert.Equal("LM1", mark.InstanceName);
        Assert.Equal(0.0, mark.Pos.X);
        Assert.Equal(0.8, mark.Pos.Y);
    }

    [Fact]
    public void Parses_curve_endpoints_and_control_points()
    {
        using var json = JsonStream(
            """
            {
              "header": {
                "mapType": "2D-Map",
                "mapName": "guangzhou606",
                "minPos": { "x": 0, "y": 0 },
                "maxPos": { "x": 10, "y": 10 },
                "resolution": 0.02,
                "version": "1.0.6"
              },
              "advancedCurveList": [
                {
                  "instanceName": "LM1-LM2",
                  "startPos": { "instanceName": "LM1", "pos": { "x": 1, "y": 2 } },
                  "endPos": { "instanceName": "LM2", "pos": { "x": 8, "y": 9 } },
                  "controlPos1": { "x": 3, "y": 4 },
                  "controlPos2": { "x": 5, "y": 6 },
                  "property": [
                    { "key": "direction", "int32Value": 1 },
                    { "key": "movestyle", "int32Value": 2 }
                  ]
                }
              ]
            }
            """);

        var document = SmapParser.Parse(json);

        var curve = Assert.Single(document.Curves);
        Assert.Equal("LM1-LM2", curve.InstanceName);
        Assert.Equal("LM1", curve.StartMark);
        Assert.Equal("LM2", curve.EndMark);
        Assert.Equal(new MapPoint(1, 2), curve.Start);
        Assert.Equal(new MapPoint(8, 9), curve.End);
        Assert.Equal(new MapPoint(3, 4), curve.Control1);
        Assert.Equal(new MapPoint(5, 6), curve.Control2);
        Assert.Equal(1, curve.Direction);
        Assert.Equal(2, curve.MoveStyle);
    }

    [Fact]
    public void Throws_smap_parse_exception_on_invalid_json()
    {
        using var json = JsonStream("not json");

        Assert.Throws<SmapParseException>(() => SmapParser.Parse(json));
    }

    private static MemoryStream JsonStream(string json) => new(Encoding.UTF8.GetBytes(json));
}
