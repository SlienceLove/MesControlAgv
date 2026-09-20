using System.IO;
using System.Text.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.DigitalTwin;

public sealed record TwinMapPoint(string Id, double X, double Y);
public sealed record TwinCalibrationPoint(string Id, double SceneX, double SceneZ);
public sealed record TwinScenePose(double X, double Y, double Z, double Yaw);
public sealed record TwinCalibrationSettings(string MapHash, string AssetHash,
    TwinCalibrationPoint[] Points, double FloorY, double ReferenceX, double ReferenceZ,
    double HeadingOffsetRadians, bool Confirmed);

/// <summary>Rigid metre-scale fit from (map x, -map y) to scene (x,z). No shear or scale correction.</summary>
public sealed record TwinCalibration(double Theta, double Tx, double Tz, double MaxResidual,
    TwinCalibrationSettings Settings)
{
    public const string MapMd5 = "9BD67A8B01F4DA2617CE67E5F8A8D6B1";
    public const string AssetSha256 = "96ca7f34c189c9c1add62c29441a7a9b16e67619c96659c259dca7ea8412671a";
    public static IReadOnlyList<TwinMapPoint> Stations { get; } = Array.AsReadOnly(new[] {
        new TwinMapPoint("LM1", 0, .8), new("LM7", 5.344, -1.174), new("LM2", 6.148, -1.304),
        new("LM6", 3.671, 1.085), new("LM4", -2.989, -1.102), new("LM5", -2.989, -2.798) });

    public static TwinCalibration Fit(TwinCalibrationSettings settings, bool requireConfirmed = true)
    {
        if (requireConfirmed && !settings.Confirmed) throw new ArgumentException("标定尚未人工确认。");
        if (!string.Equals(settings.MapHash, MapMd5, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(settings.AssetHash, AssetSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("标定地图／模型指纹不匹配。");
        if (settings.Points is not { Length: >= 3 and <= 6 } points || points.Any(p => p is null) ||
            points.Select(p => p.Id).Distinct().Count() != points.Length)
            throw new ArgumentException("至少选择三个不同对应点。");
        if (!new[] { settings.FloorY, settings.ReferenceX, settings.ReferenceZ, settings.HeadingOffsetRadians }
            .All(double.IsFinite) || Math.Abs(settings.FloorY) > 10 || Math.Abs(settings.ReferenceX) > 2 || Math.Abs(settings.ReferenceZ) > 2)
            throw new ArgumentException("地面高度／底盘参考点偏移无效。");
        var map = points.Select(p => Stations.SingleOrDefault(s => s.Id == p.Id)
            ?? throw new ArgumentException("未知地图站点。")).ToArray();
        if (points.Any(p => !double.IsFinite(p.SceneX) || !double.IsFinite(p.SceneZ) || Math.Abs(p.SceneX) > 1000 || Math.Abs(p.SceneZ) > 1000))
            throw new ArgumentException("三维对应点无效。");
        // Reject thin triangles: at least one point must be 0.5 m off the longest baseline.
        double longest = 0; int a = 0, b = 0;
        for (var i = 0; i < map.Length; i++) for (var j = i + 1; j < map.Length; j++)
        {
            var distance = Math.Sqrt(Math.Pow(map[i].X - map[j].X, 2) + Math.Pow(map[i].Y - map[j].Y, 2));
            if (distance > longest) { longest = distance; a = i; b = j; }
        }
        if (longest < 1 || map.Max(p => Math.Abs((map[b].X-map[a].X)*(p.Y-map[a].Y) -
            (map[b].Y-map[a].Y)*(p.X-map[a].X)) / longest) < .5)
            throw new ArgumentException("对应点接近共线，请补充另一方向的站点（如 LM6／LM4／LM5）。");
        var mx = map.Average(p => p.X); var mv = map.Average(p => -p.Y);
        var sx = points.Average(p => p.SceneX); var sz = points.Average(p => p.SceneZ);
        double dot = 0, cross = 0;
        for (var i = 0; i < map.Length; i++)
        {
            var u = map[i].X-mx; var v = -map[i].Y-mv;
            var x = points[i].SceneX-sx; var z = points[i].SceneZ-sz;
            dot += u*x+v*z; cross += u*z-v*x;
        }
        var theta = Math.Atan2(cross, dot); var c = Math.Cos(theta); var s = Math.Sin(theta);
        var tx = sx-c*mx+s*mv; var tz = sz-s*mx-c*mv;
        var residual = points.Select((p,i) => Math.Sqrt(Math.Pow(c*map[i].X+s*map[i].Y+tx-p.SceneX,2) +
            Math.Pow(s*map[i].X-c*map[i].Y+tz-p.SceneZ,2))).Max();
        if (!double.IsFinite(residual) || residual > .15) throw new ArgumentException($"最大偏差 {residual:F3} m 超过 0.15 m，请核对对应点及模型比例。");
        return new(theta, tx, tz, residual, settings with { Points = points.ToArray() });
    }

    public TwinScenePose Project(AgvPoseResponse pose)
    {
        if (pose.X is not { } x || pose.Y is not { } y || pose.Angle is not { } angle ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(angle)) throw new ArgumentException("无效位姿。");
        var yaw = NormalizeAngle(angle - Theta + Settings.HeadingOffsetRadians);
        var c = Math.Cos(Theta); var s = Math.Sin(Theta);
        var cy = Math.Cos(yaw); var sy = Math.Sin(yaw);
        return new(c*x+s*y+Tx - cy*Settings.ReferenceX-sy*Settings.ReferenceZ,
            Settings.FloorY, s*x-c*y+Tz + sy*Settings.ReferenceX-cy*Settings.ReferenceZ, yaw);
    }
    public static double NormalizeAngle(double value) => Math.Atan2(Math.Sin(value), Math.Cos(value));
    public static void Save(string path, TwinCalibrationSettings settings)
    {
        _ = Fit(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static TwinCalibration Load(string path) => Fit(
        JsonSerializer.Deserialize<TwinCalibrationSettings>(File.ReadAllText(path)) ?? throw new ArgumentException("空标定文件。"));
}
