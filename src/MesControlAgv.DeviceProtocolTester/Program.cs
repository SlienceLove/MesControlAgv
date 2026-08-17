using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

return await ProbeProgram.RunAsync(args);

internal static class ProbeProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (FormatException exception)
        {
            Console.Error.WriteLine($"参数错误：{exception.Message}");
            PrintUsage();
            return 2;
        }
        if (options.Error is not null)
        {
            Console.Error.WriteLine($"参数错误：{options.Error}");
            PrintUsage();
            return 2;
        }

        if (options.Mode == ProbeMode.Write && !options.ConfirmWrite)
        {
            Console.Error.WriteLine("写入模式必须显式追加 --confirm-write I-UNDERSTAND；验证分支默认只读。");
            return 2;
        }

        try
        {
            var result = options.Transport switch
            {
                ProbeTransport.Tcp => await ProbeTcpAsync(options),
                ProbeTransport.Http => await ProbeHttpAsync(options),
                _ => throw new InvalidOperationException("未支持的传输类型。")
            };

            var json = JsonSerializer.Serialize(result, JsonOptions);
            Console.WriteLine(json);
            if (options.OutputPath is not null)
            {
                await File.WriteAllTextAsync(options.OutputPath, json + Environment.NewLine, Encoding.UTF8);
                Console.Error.WriteLine($"会话记录已写入：{Path.GetFullPath(options.OutputPath)}");
            }

            return result.Success ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or SocketException or HttpRequestException or TaskCanceledException or TimeoutException or FormatException)
        {
            var result = new ProbeResult(
                false, options.Transport.ToString().ToLowerInvariant(), options.Endpoint, ModeName(options.Mode),
                DateTimeOffset.UtcNow, 0, null, null, null, null, exception.GetType().Name, exception.Message);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 1;
        }
    }

    private static async Task<ProbeResult> ProbeTcpAsync(ProbeOptions options)
    {
        using var client = new TcpClient();
        var stopwatch = Stopwatch.StartNew();
        await client.ConnectAsync(options.Host!, options.Port).WaitAsync(options.Timeout);
        await using var stream = client.GetStream();
        var request = options.Payload!;
        await stream.WriteAsync(request);
        await stream.FlushAsync();

        using var responseBuffer = new MemoryStream();
        var readBuffer = new byte[4096];
        using var readDeadline = new CancellationTokenSource(options.Timeout);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(readBuffer, readDeadline.Token);
                if (read == 0) break;
                responseBuffer.Write(readBuffer, 0, read);
                if (options.StopAtNewLine && responseBuffer.GetBuffer().AsSpan(0, (int)responseBuffer.Length).Contains((byte)'\n')) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Some instruments keep a session socket open. A timeout after bytes arrived is a valid capture boundary.
        }

        stopwatch.Stop();
        return ProbeResult.FromBytes(true, "tcp", options.Endpoint, options.Mode, request, responseBuffer.ToArray(), stopwatch.ElapsedMilliseconds);
    }

    private static async Task<ProbeResult> ProbeHttpAsync(ProbeOptions options)
    {
        using var client = new HttpClient { Timeout = options.Timeout };
        using var request = new HttpRequestMessage(new HttpMethod(options.HttpMethod!), new Uri(options.Endpoint));
        if (options.Payload.Length > 0)
        {
            request.Content = new ByteArrayContent(options.Payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(options.ContentType!);
        }

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        stopwatch.Stop();
        return ProbeResult.FromBytes(response.IsSuccessStatusCode, "http", options.Endpoint, options.Mode, options.Payload, responseBytes,
            stopwatch.ElapsedMilliseconds, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("离子色谱直连验证器（默认只读，不依赖 MES）");
        Console.WriteLine();
        Console.WriteLine("TCP：");
        Console.WriteLine("  dotnet run --project src/MesControlAgv.DeviceProtocolTester -- tcp --host 192.168.1.50 --port 9000 --request-text \"STATUS\\r\\n\"");
        Console.WriteLine("HTTP：");
        Console.WriteLine("  dotnet run --project src/MesControlAgv.DeviceProtocolTester -- http --url http://192.168.1.50:8080/api/status");
        Console.WriteLine("通用参数：--mode read-only|write --confirm-write I-UNDERSTAND --timeout-ms 3000 --output session.json");
        Console.WriteLine("TCP 参数：--request-text TEXT 或 --request-hex HEX；--stop-at-newline");
        Console.WriteLine("HTTP 参数：--method GET|POST --body TEXT 或 --body-file PATH --content-type MIME");
    }

    private static string ModeName(ProbeMode mode) => mode == ProbeMode.Write ? "write" : "read-only";
}

internal enum ProbeTransport { Tcp, Http }
internal enum ProbeMode { ReadOnly, Write }

internal sealed class ProbeOptions
{
    public ProbeTransport Transport { get; private init; }
    public ProbeMode Mode { get; private init; } = ProbeMode.ReadOnly;
    public string Endpoint { get; private init; } = "";
    public string? Host { get; private init; }
    public int Port { get; private init; }
    public string? HttpMethod { get; private init; }
    public string ContentType { get; private init; } = "application/json";
    public byte[] Payload { get; private init; } = [];
    public TimeSpan Timeout { get; private init; } = TimeSpan.FromSeconds(3);
    public bool StopAtNewLine { get; private init; }
    public bool ConfirmWrite { get; private init; }
    public string? OutputPath { get; private init; }
    public string? Error { get; private init; }

    public static ProbeOptions Parse(string[] args)
    {
        var transport = args[0].Equals("tcp", StringComparison.OrdinalIgnoreCase) ? ProbeTransport.Tcp :
            args[0].Equals("http", StringComparison.OrdinalIgnoreCase) ? ProbeTransport.Http : (ProbeTransport)(-1);
        if ((int)transport < 0) return new() { Error = "第一个参数必须是 tcp 或 http。" };

        string? host = null, url = null, method = null, text = null, hex = null, bodyFile = null, body = null, output = null;
        var port = 0;
        var mode = ProbeMode.ReadOnly;
        var timeoutMs = 3000;
        var contentType = "application/json";
        var stopAtNewLine = false;
        var confirmWrite = false;
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i].ToLowerInvariant();
            var takesValue = key is not "--stop-at-newline";
            var value = takesValue && i + 1 < args.Length ? args[++i] : null;
            switch (key)
            {
                case "--host": host = value; break;
                case "--port": if (!int.TryParse(value, out port)) return new() { Error = "--port 不是整数。" }; break;
                case "--url": url = value; break;
                case "--method": method = value?.ToUpperInvariant(); break;
                case "--request-text": text = value; break;
                case "--request-hex": hex = value; break;
                case "--body": body = value; break;
                case "--body-file": bodyFile = value; break;
                case "--content-type": contentType = value ?? contentType; break;
                case "--timeout-ms": if (!int.TryParse(value, out timeoutMs) || timeoutMs <= 0) return new() { Error = "--timeout-ms 必须是正整数。" }; break;
                case "--mode":
                    if (value?.Equals("write", StringComparison.OrdinalIgnoreCase) == true) mode = ProbeMode.Write;
                    else if (value?.Equals("read-only", StringComparison.OrdinalIgnoreCase) == true || value?.Equals("readonly", StringComparison.OrdinalIgnoreCase) == true) mode = ProbeMode.ReadOnly;
                    else return new() { Error = "--mode 必须是 read-only 或 write。" };
                    break;
                case "--confirm-write": confirmWrite = value == "I-UNDERSTAND"; break;
                case "--stop-at-newline": stopAtNewLine = true; break;
                case "--output": output = value; break;
                default: return new() { Error = $"未知参数：{key}" };
            }
        }

        if (transport == ProbeTransport.Tcp)
        {
            if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) return new() { Error = "TCP 必须提供有效的 --host 和 --port。" };
            if (text is not null && hex is not null) return new() { Error = "--request-text 与 --request-hex 只能选一个。" };
            var payload = hex is not null ? ParseHex(hex) : Encoding.UTF8.GetBytes(Unescape(text ?? ""));
            return new() { Transport = transport, Mode = mode, Endpoint = $"tcp://{host}:{port}", Host = host, Port = port, Payload = payload, Timeout = TimeSpan.FromMilliseconds(timeoutMs), StopAtNewLine = stopAtNewLine, ConfirmWrite = confirmWrite, OutputPath = output };
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return new() { Error = "HTTP 必须提供 http/https 的 --url。" };
        if (method is null) method = "GET";
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")) return new() { Error = "--method 不受支持。" };
        if (body is not null && bodyFile is not null) return new() { Error = "--body 与 --body-file 只能选一个。" };
        if (bodyFile is not null && !File.Exists(bodyFile)) return new() { Error = $"找不到 body 文件：{bodyFile}" };
        var bodyBytes = bodyFile is not null ? File.ReadAllBytes(bodyFile) : Encoding.UTF8.GetBytes(body ?? "");
        return new() { Transport = transport, Mode = mode, Endpoint = uri.ToString(), HttpMethod = method, Payload = bodyBytes, ContentType = contentType, Timeout = TimeSpan.FromMilliseconds(timeoutMs), ConfirmWrite = confirmWrite, OutputPath = output };
    }

    private static string Unescape(string value) => value.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");
    private static byte[] ParseHex(string value)
    {
        var cleaned = value.Replace(" ", "").Replace("-", "");
        if (cleaned.Length % 2 != 0) throw new FormatException("十六进制报文长度必须为偶数。");
        return Convert.FromHexString(cleaned);
    }
}

internal sealed record ProbeResult(
    bool Success, string Transport, string Endpoint, string Mode, DateTimeOffset TimestampUtc,
    long ElapsedMs, string? RequestBase64, string? ResponseBase64, string? ResponseText,
    string? Detail, string? ErrorType, string? Error)
{
    public static ProbeResult FromBytes(bool success, string transport, string endpoint, ProbeMode mode, byte[] request, byte[] response, long elapsedMs, string? detail = null) =>
        new(success, transport, endpoint, mode == ProbeMode.Write ? "write" : "read-only", DateTimeOffset.UtcNow, elapsedMs,
            Convert.ToBase64String(request), Convert.ToBase64String(response), Decode(response), detail, null, null);

    private static string? Decode(byte[] bytes) => bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
}
