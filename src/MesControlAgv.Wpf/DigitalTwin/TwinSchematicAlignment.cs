using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.DigitalTwin;

/// <summary>User-approved workstation footprint alignment, not measured calibration.</summary>
public static class TwinSchematicAlignment
{
    // Map island centre ≈ (3.27, -1.58); CAD workbench centre ≈ (18.18, -7.04).
    // Match their principal edges at unit scale. Do not use the instrument's raised centre
    // as the table footprint centre. User confirmed this reference on 2026-09-18.
    public const double Theta = Math.PI;
    public const double Tx = 21.45;
    public const double Tz = -5.46;
    public const double FloorY = -.025;

    public static TwinScenePose Project(AgvPoseResponse pose)
    {
        if (pose.X is not { } x || pose.Y is not { } y || pose.Angle is not { } angle ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(angle))
            throw new ArgumentException("无效实时坐标。");
        var c = Math.Cos(Theta); var s = Math.Sin(Theta);
        return new(c*x+s*y+Tx, FloorY, s*x-c*y+Tz, TwinCalibration.NormalizeAngle(angle-Theta));
    }
}
