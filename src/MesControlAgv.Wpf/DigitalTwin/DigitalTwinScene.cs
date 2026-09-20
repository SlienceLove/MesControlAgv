using System.IO;
using System.Text.Json;

namespace MesControlAgv.Wpf.DigitalTwin;

public sealed record DigitalTwinDevice(string Id, string Name, string Description);

/// <summary>Presentation-only catalog. IDs are model nodes, not confirmed equipment codes.</summary>
public static class DigitalTwinScene
{
    public const string Host = "digital-twin.local";
    public const string Page = "https://digital-twin.local/index.html";
    public static IReadOnlyList<DigitalTwinDevice> Devices { get; } =
    [
        new("agv-composite-a", "AGV＋机械臂整体", "前侧复合机器人，车体、机械臂和料架作为一个对象。"),
        new("d160-a", "D160 离子色谱仪 · 未绑定", "未绑定／通信待完成。保留静态展示，不使用 ShineLab 控制电脑 STN61_01 的状态代替仪器状态。"),
        new("autosampler-a", "SHA18i 自动进样器 · 未绑定", "较矮、带托盘的自动进样器。未绑定／通信待完成，不使用 D160 或 ShineLab 控制电脑 STN61_01 的状态。"),
        new("decapper-a", "开盖／分液工作站", "工作站整机，包含伸出的托盘。")
    ];

    public static DigitalTwinDevice? Find(string? id) => Devices.FirstOrDefault(d => d.Id == id);

    public static bool IsLocalResource(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == Host && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsViewerPage(string? address) =>
        IsLocalResource(address) && new Uri(address!).AbsolutePath == "/index.html" &&
        string.IsNullOrEmpty(new Uri(address!).Query);

    public static bool TryReadMessage(string source, string json, out string kind, out string? deviceId)
    {
        kind = "";
        deviceId = null;
        if (!IsViewerPage(source) || json.Length > 4096) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String) return false;
            var message = type.GetString();
            if (message is "ready" or "load-error" or "fullscreen-toggle" or "fullscreen-exit") { kind = message; return true; }
            if (message != "selected" || !root.TryGetProperty("id", out var id)) return false;
            if (id.ValueKind == JsonValueKind.Null) { kind = "selected"; return true; }
            if (id.ValueKind != JsonValueKind.String || Find(id.GetString()) is null) return false;
            deviceId = id.GetString();
            kind = "selected";
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static string? FindMissingAsset(string directory)
    {
        string[] required = ["index.html", "viewer.js", "camera-framing.mjs", "pose-motion.mjs", "map-ground.js", "map-ground-data.mjs", "map-ground.json", "viewer.css", "lab606.glb",
            "vendor/three/build/three.module.min.js", "vendor/three/build/three.core.min.js",
            "vendor/three/examples/jsm/loaders/GLTFLoader.js",
            "vendor/three/examples/jsm/loaders/DRACOLoader.js",
            "vendor/three/examples/jsm/controls/OrbitControls.js",
            "vendor/three/examples/jsm/utils/BufferGeometryUtils.js",
            "vendor/three/examples/jsm/libs/draco/gltf/draco_wasm_wrapper.js",
            "vendor/three/examples/jsm/libs/draco/gltf/draco_decoder.wasm"];
        return required.FirstOrDefault(file => !File.Exists(Path.Combine(directory, file)));
    }
}
