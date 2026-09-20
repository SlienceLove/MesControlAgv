using MesControlAgv.Contracts;
using MesControlAgv.Wpf.DigitalTwin;

namespace MesControlAgv.Wpf.Tests;

public sealed class TwinSchematicAlignmentTests
{
    private static AgvPoseResponse Pose(double x,double y,double angle=0) =>
        new("AGV-01",x,y,angle,.95,null,null,DateTimeOffset.UtcNow,"guangzhou606",TwinCalibration.MapMd5,DateTimeOffset.UtcNow);
    [Fact]
    public void Known_station_candidates_preserve_real_map_distance()
    {
        var a=TwinSchematicAlignment.Project(Pose(0,.8));var b=TwinSchematicAlignment.Project(Pose(3.671,1.085));
        Assert.Equal(21.45,a.X,9);Assert.Equal(-4.66,a.Z,9);
        Assert.Equal(17.779,b.X,9);Assert.Equal(-4.375,b.Z,9);
        Assert.Equal(Math.Sqrt(3.671*3.671+.285*.285),Math.Sqrt(Math.Pow(b.X-a.X,2)+Math.Pow(b.Z-a.Z,2)),9);
    }
    [Fact]
    public void Confirmed_workstation_reference_matches_table_centre_and_principal_edges()
    {
        var centre=TwinSchematicAlignment.Project(Pose(3.27,-1.58));
        Assert.Equal(18.18,centre.X,9);Assert.Equal(-7.04,centre.Z,9);
        var along=TwinSchematicAlignment.Project(Pose(4.27,-1.58));
        Assert.Equal(centre.X-1,along.X,9);Assert.Equal(centre.Z,along.Z,9);
    }
    [Fact]
    public void Any_real_path_point_is_transformed_without_snapping_to_planned_routes()
    {
        var path=new[]{Pose(0,.8,.2),Pose(.42,1.13,.4),Pose(1.07,.72,-3.13),Pose(1.07,.72,3.13)};
        for(var i=1;i<path.Length;i++)
        {
            var a=TwinSchematicAlignment.Project(path[i-1]);var b=TwinSchematicAlignment.Project(path[i]);
            var dx=path[i].X!.Value-path[i-1].X!.Value;var dy=path[i].Y!.Value-path[i-1].Y!.Value;
            Assert.Equal(Math.Sqrt(dx*dx+dy*dy),Math.Sqrt(Math.Pow(b.X-a.X,2)+Math.Pow(b.Z-a.Z,2)),9);
            Assert.Equal(TwinCalibration.NormalizeAngle(path[i].Angle!.Value-path[i-1].Angle!.Value),TwinCalibration.NormalizeAngle(b.Yaw-a.Yaw),9);
        }
    }
    [Fact]
    public void Invalid_values_are_rejected_and_do_not_become_origin_pose()
    {
        foreach(var invalid in new[]{Pose(0,.8) with{X=null},Pose(double.NaN,.8),Pose(0,double.PositiveInfinity),Pose(0,.8,double.NaN)})
            Assert.Throws<ArgumentException>(()=>TwinSchematicAlignment.Project(invalid));
    }
}
