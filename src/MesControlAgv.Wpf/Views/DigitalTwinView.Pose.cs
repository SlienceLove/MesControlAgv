using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.DigitalTwin;

namespace MesControlAgv.Wpf.Views;

public partial class DigitalTwinView
{
    private DigitalTwinPoseSession? _poseSession;
    private AgvPoseResponse? _pose;
    private string? _poseError;
    private DateTimeOffset? _lastSentPose;
    private TwinScenePose? _lastScenePose;
    private TwinCalibration? _calibration;
    private readonly Dictionary<string, TwinCalibrationPoint> _points = new();
    private bool _calibrationEditing, _posePaused, _loadingCalibration;
    private string? _pickToken, _pickStation;
    private string? _lastGroundMessage;
    private bool _schematicFollowing;
    public bool IsSchematicFollowing => _schematicFollowing;
    public void EnableSchematicFollowing(bool enabled)
    {
        if (_schematicFollowing == enabled) return;
        if (enabled)
        {
            try { VerifyAsset(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { SchematicFollowCheck.IsChecked = false; PoseStatusText.Text = $"无法启用示意跟随：{ex.Message}"; return; }
        }
        _schematicFollowing = enabled;
        SchematicFollowCheck.IsChecked = enabled;
        _lastGroundMessage = null; _lastSentPose = null; _lastScenePose = null; _posePaused = false;
        SendPoseMessage(new { type = "pose-stop" });
        if (!enabled && _calibration is null) SendPoseMessage(new { type = "pose-reset" });
        UpdatePose();
    }
    private void SchematicFollow_Changed(object sender, RoutedEventArgs e) => EnableSchematicFollowing(SchematicFollowCheck.IsChecked == true);
    private static string CalibrationPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MesControlAgv", "DigitalTwin", "606-agv-calibration.json");

    private void InitializeCalibration()
    {
        CalibrationStation.ItemsSource = TwinCalibration.Stations;
        CalibrationStation.SelectedIndex = 0;
        if (!File.Exists(CalibrationPath)) return;
        try
        {
            _loadingCalibration = true;
            VerifyAsset();
            _calibration = TwinCalibration.Load(CalibrationPath);
            var s = _calibration.Settings;
            foreach (var point in s.Points) _points[point.Id] = point;
            CalibrationFloor.Text = s.FloorY.ToString(CultureInfo.InvariantCulture);
            CalibrationReferenceX.Text = s.ReferenceX.ToString(CultureInfo.InvariantCulture);
            CalibrationReferenceZ.Text = s.ReferenceZ.ToString(CultureInfo.InvariantCulture);
            CalibrationHeading.Text = (s.HeadingOffsetRadians * 180 / Math.PI).ToString(CultureInfo.InvariantCulture);
            CalibrationConfirmed.IsChecked = true;
            ShowPoints();
            CalibrationStatusText.Text = $"已加载已确认标定 · 最大偏差 {_calibration.MaxResidual:F3} m";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { _calibration = null; CalibrationStatusText.Text = $"标定未启用：{ex.Message}"; }
        finally { _loadingCalibration = false; }
    }
    private void CalibrationNumber_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loadingCalibration || CalibrationConfirmed is null) return;
        CalibrationConfirmed.IsChecked = false;
        CancelPick();
        if (ReferenceEquals(sender, CalibrationFloor)) { _points.Clear(); ShowPoints(); }
        CalibrationStatusText.Text = "标定参数已改变，请重新核对并勾选确认；改变地面高度需重新选点。";
    }
    private static void VerifyAsset()
    {
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "DigitalTwin", "Web", "lab606.glb"));
        if (!Convert.ToHexString(SHA256.HashData(input)).Equals(TwinCalibration.AssetSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("模型已变化，须重新确认标定和模型枢轴。");
    }
    private void StartPose()
    {
        if (StatusSource is not IDigitalTwinPoseSource source) { PoseStatusText.Text = "位置接口未就绪 · 三维未同步"; return; }
        var session = new DigitalTwinPoseSession(source);
        _poseSession = session;
        session.Reading += (pose, error) =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_poseSession != session || !IsLoaded || !IsVisible || !State.IsReady) return;
                _pose = pose; _poseError = error; UpdatePose();
            });
        };
        session.Start();
    }
    private void StopPose()
    {
        _presentationPoseWarning = "等待位置更新";
        UpdatePresentationWarning();
        var session = _poseSession; _poseSession = null; session?.Dispose();
        _pose = null; _lastSentPose = null; _lastScenePose = null;
        _lastGroundMessage = null;
        SendPoseMessage(new { type = "pose-stop" });
    }
    private void UpdatePose()
    {
        if (!State.IsReady || !IsVisible) return;
        UpdateGroundCalibration();
        var now = DateTimeOffset.UtcNow;
        var error = _pose is { } pose ? TwinPoseValidation.Rejection(pose, StatusSource?.Bindings.AgvId ?? "AGV-01", now)
            : _poseError ?? "等待只读位置";
        var raw = _pose is null ? "" : $" · x={_pose.X:F3} y={_pose.Y:F3} m · 航向={_pose.Angle * 180 / Math.PI:F1}° · 置信度={_pose.Confidence:P0}";
        if (error is not null || (_calibration is null && !_schematicFollowing) || _calibrationEditing || _posePaused)
        {
            _presentationPoseWarning = error ?? (_calibrationEditing ? "选点中 · 位置同步暂停" : _posePaused ? "位置同步已暂停" : "位置未同步 · 未启用定位转换");
            UpdatePresentationWarning();
            PoseStatusText.Text = (_schematicFollowing ? "示意定位 · " : "") + (error ?? (_calibrationEditing ? "选点中，位置同步暂停" : _posePaused ? "位置同步已暂停" : "坐标已接入／三维未标定")) + raw;
            SendPoseMessage(new { type = "pose-stop" });
            return;
        }
        var target = _schematicFollowing ? TwinSchematicAlignment.Project(_pose!) : _calibration!.Project(_pose!);
        if (_lastScenePose is { } previous &&
            (Math.Sqrt(Math.Pow(target.X-previous.X,2)+Math.Pow(target.Z-previous.Z,2)) > 1.5 ||
             Math.Abs(TwinCalibration.NormalizeAngle(target.Yaw-previous.Yaw)) > Math.PI * .75))
        {
            _posePaused = true;
            _presentationPoseWarning = "定位跳变 · 已暂停同步，请退出全屏后核对";
            UpdatePresentationWarning();
            PoseStatusText.Text = "定位发生大幅跳变，请核对后点击恢复位置同步" + raw;
            SendPoseMessage(new { type = "pose-stop" });
            return;
        }
        PoseStatusText.Text = (_schematicFollowing ? "示意定位 · 正在跟随实机坐标（非测量标定）" : "真实坐标跟随（仅三维显示）") + raw;
        _presentationPoseWarning = null;
        UpdatePresentationWarning();
        if (_lastSentPose == _pose!.ReceivedAt) return;
        var snap = _lastScenePose is null;
        _lastSentPose = _pose.ReceivedAt; _lastScenePose = target;
        SendPoseMessage(new { type = "pose", x = target.X, y = target.Y, z = target.Z, yaw = target.Yaw,
            snap,
            validForMs = Math.Clamp(2000 - (now-_pose.ReceivedAt).TotalMilliseconds, 0, 2000) });
    }
    private void SendPoseMessage(object message)
    {
        if (!State.IsReady || _browser?.CoreWebView2 is not { } core) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (InvalidOperationException) { PoseStatusText.Text = "三维视窗不可用，位置更新已停止"; }
    }
    private void UpdateGroundCalibration()
    {
        object? transform = !_calibrationEditing && _schematicFollowing ? new {
            theta = TwinSchematicAlignment.Theta, tx = TwinSchematicAlignment.Tx, tz = TwinSchematicAlignment.Tz,
            floor = TwinSchematicAlignment.FloorY, md5 = TwinCalibration.MapMd5
        } : _calibration is { } c && !_calibrationEditing ? new {
            theta = c.Theta, tx = c.Tx, tz = c.Tz, floor = c.Settings.FloorY, md5 = TwinCalibration.MapMd5
        } : null;
        var alignmentKind = transform is null ? "none" : _schematicFollowing ? "schematic" : "confirmed";
        var json = JsonSerializer.Serialize(new { transform, alignmentKind });
        if (json == _lastGroundMessage) return;
        _lastGroundMessage = json;
        SendPoseMessage(new { type = "ground-calibration", transform, alignmentKind });
    }
    private void Calibration_Expanded(object sender, RoutedEventArgs e)
    { _calibrationEditing = true; SendPoseMessage(new { type = "pose-reset" }); UpdatePose(); }
    private void Calibration_Collapsed(object sender, RoutedEventArgs e)
    { _calibrationEditing = false; CancelPick(); _lastSentPose = null; _lastScenePose = null; UpdatePose(); }
    private void CancelPick()
    { _pickToken = null; _pickStation = null; SendPoseMessage(new { type = "cal-cancel" }); }
    private static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new ArgumentException("请输入有限数值，使用小数点。");
    private void PickCalibration_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!State.IsReady || CalibrationStation.SelectedItem is not TwinMapPoint station) return;
            var floor = Number(CalibrationFloor.Text);
            if (Math.Abs(floor) > 10) throw new ArgumentException("地面高度超出范围。");
            _pickToken = Guid.NewGuid().ToString("N"); _pickStation = station.Id;
            SendPoseMessage(new { type = "cal-pick", token = _pickToken, floor });
            CalibrationStatusText.Text = $"请点击 {station.Id} 的地面位置；点击射线与指定地面相交，不采用设备表面高度。";
        }
        catch (ArgumentException ex) { CalibrationStatusText.Text = ex.Message; }
    }
    private bool HandleCalibrationPoint(string source, string json)
    {
        if (!DigitalTwinScene.IsViewerPage(source) || json.Length > 4096 || !_calibrationEditing || _pickToken is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type",out var type) || type.GetString() != "cal-point") return false;
            if (!root.TryGetProperty("token",out var token) || token.GetString() != _pickToken ||
                !root.TryGetProperty("x",out var x) || !root.TryGetProperty("z",out var z) ||
                x.ValueKind != JsonValueKind.Number || z.ValueKind != JsonValueKind.Number ||
                !x.TryGetDouble(out var px) || !z.TryGetDouble(out var pz) || !double.IsFinite(px) || !double.IsFinite(pz) ||
                Math.Abs(px) > 1000 || Math.Abs(pz) > 1000) return false;
            _points[_pickStation!] = new(_pickStation!,px,pz);
            CalibrationConfirmed.IsChecked = false; CancelPick(); ShowPoints();
            CalibrationStatusText.Text = "已记录选点；检查偏差并核对车头与参考点后保存。";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }
    private void ShowPoints() => CalibrationPointsText.Text = _points.Count == 0 ? "尚未选点" :
        string.Join("  |  ", _points.Values.Select(p => $"{p.Id}: X={p.SceneX:F3}, Z={p.SceneZ:F3}"));
    private TwinCalibrationSettings ReadSettings() => new(TwinCalibration.MapMd5, TwinCalibration.AssetSha256,
        _points.Values.ToArray(), Number(CalibrationFloor.Text), Number(CalibrationReferenceX.Text),
        Number(CalibrationReferenceZ.Text), Number(CalibrationHeading.Text)*Math.PI/180, CalibrationConfirmed.IsChecked == true);
    private void CheckCalibration_Click(object sender, RoutedEventArgs e)
    {
        try { var fit = TwinCalibration.Fit(ReadSettings(), false); CalibrationStatusText.Text = $"最大对应点偏差 {fit.MaxResidual:F3} m；请继续核对参考点和朝向，检查偏差不等于已启用。"; }
        catch (ArgumentException ex) { CalibrationStatusText.Text = ex.Message; }
    }
    private void SaveCalibration_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            VerifyAsset(); var settings = ReadSettings(); var calibration = TwinCalibration.Fit(settings);
            TwinCalibration.Save(CalibrationPath, settings); _calibration = calibration;
            EnableSchematicFollowing(false);
            _lastSentPose = null; _lastScenePose = null; _posePaused = false;
            CalibrationStatusText.Text = $"已保存 · 最大偏差 {calibration.MaxResidual:F3} m；收起标定面板后跟随真实位置。";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { CalibrationStatusText.Text = $"未保存：{ex.Message}"; }
    }
    private void ClearCalibration_Click(object sender, RoutedEventArgs e)
    { _points.Clear(); CalibrationConfirmed.IsChecked = false; CancelPick(); ShowPoints(); CalibrationStatusText.Text = "草稿已清空；已保存标定不变。"; }
    private void PausePose_Click(object sender, RoutedEventArgs e) { _posePaused = true; UpdatePose(); }
    private void ResumePose_Click(object sender, RoutedEventArgs e)
    { _posePaused = false; _lastScenePose = null; _lastSentPose = null; UpdatePose(); }
}
