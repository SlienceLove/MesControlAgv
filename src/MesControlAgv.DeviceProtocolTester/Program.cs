using System.Diagnostics;
using System.IO.Ports;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

return await ProbeProgram.RunAsync(args);

internal static class ProbeProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        ProbeOptions options;
        try { options = ProbeOptions.Parse(args); }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"参数错误：{ex.Message}");
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
            Console.Error.WriteLine("写入模式必须显式追加 --confirm-write I-UNDERSTAND；验证器默认只读。");
            return 2;
        }

        try
        {
            var result = options.Transport switch
            {
                ProbeTransport.Tcp => await ProbeTcpAsync(options),
                ProbeTransport.Http => await ProbeHttpAsync(options),
                ProbeTransport.Serial => await ProbeSerialAsync(options),
                _ => throw new InvalidOperationException("不支持的传输类型。")
            };
            var json = JsonSerializer.Serialize(result, JsonOptions);
            Console.WriteLine(json);
            if (options.OutputPath is not null)
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(options.OutputPath, json + Environment.NewLine, Encoding.UTF8);
                Console.Error.WriteLine($"会话记录已写入：{Path.GetFullPath(options.OutputPath)}");
            }
            return result.Success ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or TaskCanceledException or TimeoutException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            var result = new ProbeResult(false, options.Transport.ToString().ToLowerInvariant(), options.Endpoint, ModeName(options.Mode),
                DateTimeOffset.UtcNow, 0, null, null, null, null, ex.GetType().Name, ex.Message);
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
        await stream.WriteAsync(options.Payload);
        await stream.FlushAsync();
        using var responseBuffer = new MemoryStream();
        var readBuffer = new byte[4096];
        using var deadline = new CancellationTokenSource(options.Timeout);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(readBuffer, deadline.Token);
                if (read == 0) break;
                responseBuffer.Write(readBuffer, 0, read);
                if (options.StopAtNewLine && responseBuffer.GetBuffer().AsSpan(0, (int)responseBuffer.Length).Contains((byte)'\n')) break;
            }
        }
        catch (OperationCanceledException) { }
        stopwatch.Stop();
        return ProbeResult.FromBytes(true, "tcp", options.Endpoint, options.Mode, options.Payload, responseBuffer.ToArray(), stopwatch.ElapsedMilliseconds);
    }

    private static async Task<ProbeResult> ProbeHttpAsync(ProbeOptions options)
    {
        using var client = new HttpClient { Timeout = options.Timeout };
        using var request = new HttpRequestMessage(new HttpMethod(options.HttpMethod!), new Uri(options.Endpoint));
        if (options.Payload.Length > 0)
        {
            request.Content = new ByteArrayContent(options.Payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(options.ContentType);
        }
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        stopwatch.Stop();
        return ProbeResult.FromBytes(response.IsSuccessStatusCode, "http", options.Endpoint, options.Mode, options.Payload, responseBytes,
            stopwatch.ElapsedMilliseconds, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private static Task<ProbeResult> ProbeSerialAsync(ProbeOptions options)
    {
        if (options.Payload.Length < 4) throw new FormatException("串口 Modbus RTU 请求至少需要地址、功能码、数据和 CRC 字节。");
        var requestModbus = ModbusRtu.Parse(options.Payload);
        if (!requestModbus.CrcValid) throw new FormatException($"请求 CRC 校验失败：计算值 {requestModbus.ComputedCrcHex}，报文值 {requestModbus.FrameCrcHex}。");
        if (options.Mode == ProbeMode.ReadOnly && ModbusRtu.IsWriteFunction(requestModbus.FunctionCode))
            throw new FormatException("只读模式禁止发送 Modbus 写功能码；如确需验证写入，请显式使用 --mode write --confirm-write I-UNDERSTAND。");

        using var port = new SerialPort(options.ComPort!, options.BaudRate, ParseParity(options.Parity), options.DataBits, ParseStopBits(options.StopBits))
        {
            ReadTimeout = (int)options.Timeout.TotalMilliseconds,
            WriteTimeout = (int)options.Timeout.TotalMilliseconds,
            DtrEnable = false,
            RtsEnable = false
        };
        var stopwatch = Stopwatch.StartNew();
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        port.Write(options.Payload, 0, options.Payload.Length);

        using var responseBuffer = new MemoryStream();
        var readBuffer = new byte[256];
        try
        {
            while (true)
            {
                var read = port.Read(readBuffer, 0, readBuffer.Length);
                if (read <= 0) break;
                responseBuffer.Write(readBuffer, 0, read);
            }
        }
        catch (TimeoutException) { }
        stopwatch.Stop();
        var response = responseBuffer.ToArray();
        var responseModbus = response.Length >= 4 ? ModbusRtu.Parse(response) : null;
        var detail = responseModbus is null
            ? "未收到完整 Modbus RTU 响应（已保存超时前收到的字节）。"
            : $"Modbus 从站 0x{responseModbus.SlaveAddress:X2}，功能码 0x{responseModbus.FunctionCode:X2}，响应长度 {response.Length} 字节，CRC {(responseModbus.CrcValid ? "正确" : "错误")}。";
        return Task.FromResult(ProbeResult.FromBytes(responseModbus?.CrcValid == true, "serial", options.Endpoint, options.Mode,
            options.Payload, response, stopwatch.ElapsedMilliseconds, detail, requestModbus, responseModbus));
    }

    private static Parity ParseParity(string value) => value.ToLowerInvariant() switch
    {
        "none" => Parity.None, "odd" => Parity.Odd, "even" => Parity.Even, "mark" => Parity.Mark, "space" => Parity.Space,
        _ => throw new FormatException("--parity 必须是 none、odd、even、mark 或 space。")
    };
    private static StopBits ParseStopBits(string value) => value switch
    {
        "1" => StopBits.One, "1.5" => StopBits.OnePointFive, "2" => StopBits.Two,
        _ => throw new FormatException("--stop-bits 必须是 1、1.5 或 2。")
    };

    private static void PrintUsage()
    {
        Console.WriteLine("离子色谱直连验证器（默认只读，不依赖 MES）");
        Console.WriteLine("TCP：dotnet run --project src/MesControlAgv.DeviceProtocolTester -- tcp --host 192.168.1.50 --port 9000 --request-text \"STATUS\\r\\n\"");
        Console.WriteLine("HTTP：dotnet run --project src/MesControlAgv.DeviceProtocolTester -- http --url http://192.168.1.50:8080/api/status");
        Console.WriteLine("串口 Modbus RTU：dotnet run --project src/MesControlAgv.DeviceProtocolTester -- serial --com COM3 --request-hex \"01 04 19 00 00 14 F7 59\"");
        Console.WriteLine("串口参数：--baud 115200 --data-bits 8 --parity none --stop-bits 1（默认 115200 8N1）");
        Console.WriteLine("通用参数：--mode read-only|write --confirm-write I-UNDERSTAND --timeout-ms 3000 --output session.json");
    }
    private static string ModeName(ProbeMode mode) => mode == ProbeMode.Write ? "write" : "read-only";
}

internal enum ProbeTransport { Tcp, Http, Serial }
internal enum ProbeMode { ReadOnly, Write }

internal sealed class ProbeOptions
{
    public ProbeTransport Transport { get; private init; }
    public ProbeMode Mode { get; private init; } = ProbeMode.ReadOnly;
    public string Endpoint { get; private init; } = "";
    public string? Host { get; private init; }
    public string? ComPort { get; private init; }
    public int Port { get; private init; }
    public int BaudRate { get; private init; } = 115200;
    public int DataBits { get; private init; } = 8;
    public string Parity { get; private init; } = "none";
    public string StopBits { get; private init; } = "1";
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
            args[0].Equals("http", StringComparison.OrdinalIgnoreCase) ? ProbeTransport.Http :
            args[0].Equals("serial", StringComparison.OrdinalIgnoreCase) ? ProbeTransport.Serial : (ProbeTransport)(-1);
        if ((int)transport < 0) return new() { Error = "第一个参数必须是 tcp、http 或 serial。" };

        string? host = null, url = null, method = null, text = null, hex = null, bodyFile = null, body = null, output = null, com = null;
        var port = 0; var baud = 115200; var dataBits = 8; var parity = "none"; var stopBits = "1";
        var mode = ProbeMode.ReadOnly; var timeoutMs = 3000; var contentType = "application/json"; var stopAtNewLine = false; var confirmWrite = false;
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i].ToLowerInvariant();
            var takesValue = key != "--stop-at-newline";
            var value = takesValue && i + 1 < args.Length ? args[++i] : null;
            switch (key)
            {
                case "--host": host = value; break; case "--com": com = value; break;
                case "--port": if (!int.TryParse(value, out port)) return new() { Error = "--port 必须是整数。" }; break;
                case "--baud": if (!int.TryParse(value, out baud) || baud <= 0) return new() { Error = "--baud 必须是正整数。" }; break;
                case "--data-bits": if (!int.TryParse(value, out dataBits) || dataBits is < 5 or > 8) return new() { Error = "--data-bits 必须是 5 到 8。" }; break;
                case "--parity": parity = value?.ToLowerInvariant() ?? parity; break; case "--stop-bits": stopBits = value ?? stopBits; break;
                case "--url": url = value; break; case "--method": method = value?.ToUpperInvariant(); break;
                case "--request-text": text = value; break; case "--request-hex": hex = value; break;
                case "--body": body = value; break; case "--body-file": bodyFile = value; break; case "--content-type": contentType = value ?? contentType; break;
                case "--timeout-ms": if (!int.TryParse(value, out timeoutMs) || timeoutMs <= 0) return new() { Error = "--timeout-ms 必须是正整数。" }; break;
                case "--mode": if (value?.Equals("write", StringComparison.OrdinalIgnoreCase) == true) mode = ProbeMode.Write; else if (value?.Equals("read-only", StringComparison.OrdinalIgnoreCase) == true || value?.Equals("readonly", StringComparison.OrdinalIgnoreCase) == true) mode = ProbeMode.ReadOnly; else return new() { Error = "--mode 必须是 read-only 或 write。" }; break;
                case "--confirm-write": confirmWrite = value == "I-UNDERSTAND"; break; case "--stop-at-newline": stopAtNewLine = true; break; case "--output": output = value; break;
                default: return new() { Error = $"未知参数：{key}" };
            }
        }
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        if (transport == ProbeTransport.Tcp)
        {
            if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) return new() { Error = "TCP 必须提供有效的 --host 和 --port。" };
            if (text is not null && hex is not null) return new() { Error = "--request-text 与 --request-hex 只能选一个。" };
            return new() { Transport = transport, Mode = mode, Endpoint = $"tcp://{host}:{port}", Host = host, Port = port, Payload = hex is not null ? ParseHex(hex) : Encoding.UTF8.GetBytes(Unescape(text ?? "")), Timeout = timeout, StopAtNewLine = stopAtNewLine, ConfirmWrite = confirmWrite, OutputPath = output };
        }
        if (transport == ProbeTransport.Serial)
        {
            if (string.IsNullOrWhiteSpace(com)) return new() { Error = "串口模式必须提供 --com，例如 COM3。" };
            if (text is not null && hex is not null) return new() { Error = "--request-text 与 --request-hex 只能选一个。" };
            if (text is null && hex is null) return new() { Error = "串口模式必须提供 --request-hex 或 --request-text。" };
            return new() { Transport = transport, Mode = mode, Endpoint = $"serial://{com}", ComPort = com, BaudRate = baud, DataBits = dataBits, Parity = parity, StopBits = stopBits, Payload = hex is not null ? ParseHex(hex) : Encoding.UTF8.GetBytes(Unescape(text!)), Timeout = timeout, ConfirmWrite = confirmWrite, OutputPath = output };
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return new() { Error = "HTTP 必须提供 http/https 的 --url。" };
        method ??= "GET"; if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")) return new() { Error = "--method 不受支持。" };
        if (body is not null && bodyFile is not null) return new() { Error = "--body 与 --body-file 只能选一个。" };
        if (bodyFile is not null && !File.Exists(bodyFile)) return new() { Error = $"找不到 body 文件：{bodyFile}" };
        return new() { Transport = transport, Mode = mode, Endpoint = uri.ToString(), HttpMethod = method, Payload = bodyFile is not null ? File.ReadAllBytes(bodyFile) : Encoding.UTF8.GetBytes(body ?? ""), ContentType = contentType, Timeout = timeout, ConfirmWrite = confirmWrite, OutputPath = output };
    }
    private static string Unescape(string value) => value.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");
    private static byte[] ParseHex(string value) { var cleaned = value.Replace(" ", "").Replace("-", ""); if (cleaned.Length % 2 != 0) throw new FormatException("十六进制报文长度必须为偶数。"); return Convert.FromHexString(cleaned); }
}

internal sealed record ProbeResult(bool Success, string Transport, string Endpoint, string Mode, DateTimeOffset TimestampUtc, long ElapsedMs, string? RequestBase64, string? ResponseBase64, string? ResponseText, string? Detail, string? ErrorType, string? Error, ModbusFrame? RequestModbus = null, ModbusFrame? ResponseModbus = null)
{
    public static ProbeResult FromBytes(bool success, string transport, string endpoint, ProbeMode mode, byte[] request, byte[] response, long elapsedMs, string? detail = null, ModbusFrame? requestModbus = null, ModbusFrame? responseModbus = null) => new(success, transport, endpoint, mode == ProbeMode.Write ? "write" : "read-only", DateTimeOffset.UtcNow, elapsedMs, Convert.ToBase64String(request), Convert.ToBase64String(response), response.Length == 0 ? null : Encoding.UTF8.GetString(response), detail, null, null, requestModbus, responseModbus);
}

internal sealed record ModbusFrame(byte SlaveAddress, byte FunctionCode, int FrameLength, string FrameCrcHex, string ComputedCrcHex, bool CrcValid);
internal static class ModbusRtu
{
    public static bool IsWriteFunction(byte functionCode) => functionCode is 0x05 or 0x06 or 0x0F or 0x10;
    public static ModbusFrame Parse(byte[] frame) { if (frame.Length < 4) throw new FormatException("Modbus RTU 报文长度不足 4 字节。"); var supplied = (ushort)(frame[^2] | (frame[^1] << 8)); var computed = ComputeCrc(frame.AsSpan(0, frame.Length - 2)); return new(frame[0], frame[1], frame.Length, $"{supplied:X4}", $"{computed:X4}", supplied == computed); }
    public static ushort ComputeCrc(ReadOnlySpan<byte> data) { ushort crc = 0xFFFF; foreach (var value in data) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1); } return crc; }
}
