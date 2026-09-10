using System.Buffers;
using System.Text;

namespace MesControlAgv.Contracts;

/// <summary>
/// Wire formats observed/accepted by the ShineLab server.  The current YhLoop
/// client uses LF-delimited JSON; the 55AA format remains available for older
/// or offline clients.
/// </summary>
public enum ShineLabWireFormat
{
    Unknown = 0,
    LineJson = 1,
    Native55Aa = 2
}

public readonly record struct ShineLabWireFrame(
    ShineLabWireFormat Format,
    byte[] RawBytes,
    byte[] JsonBytes);

public static class ShineLabWireCodec
{
    public const int MaxLineBytes = 1_048_576;
    public const int MaxDetectionBytes = MaxLineBytes + 2;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] EncodeJson(string json, ShineLabWireFormat format)
    {
        ArgumentNullException.ThrowIfNull(json);
        return format switch
        {
            ShineLabWireFormat.LineJson => EncodeLineJson(json),
            ShineLabWireFormat.Native55Aa => ShineLabTcpFrameCodec.EncodeJson(json),
            _ => throw new InvalidOperationException(
                "Cannot encode ShineLab JSON before the connection wire format is identified.")
        };
    }

    public static bool TryDecodeJson(
        in ShineLabWireFrame frame,
        out string json,
        out string error)
    {
        if (frame.Format == ShineLabWireFormat.Native55Aa)
            return ShineLabTcpFrameCodec.TryDecodeJson(frame.RawBytes, out json, out error);

        if (frame.Format != ShineLabWireFormat.LineJson)
        {
            json = string.Empty;
            error = "ShineLab wire format is unknown.";
            return false;
        }

        try
        {
            json = StrictUtf8.GetString(frame.JsonBytes);
            error = string.Empty;
            return true;
        }
        catch (DecoderFallbackException exception)
        {
            json = string.Empty;
            error = $"Line-delimited ShineLab JSON is not valid UTF-8: {exception.Message}";
            return false;
        }
    }

    private static byte[] EncodeLineJson(string json)
    {
        var jsonBytes = StrictUtf8.GetBytes(json);
        if (jsonBytes.Length > MaxLineBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(json),
                jsonBytes.Length,
                $"ShineLab line JSON cannot exceed {MaxLineBytes} UTF-8 bytes.");
        }

        var frame = new byte[jsonBytes.Length + 1];
        jsonBytes.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        return frame;
    }
}

/// <summary>
/// Incremental LF-delimited JSON reader.  The returned raw bytes include the
/// LF delimiter; JsonBytes excludes LF and an optional CR immediately before
/// it.  The reader never parses or executes JSON itself.
/// </summary>
public sealed class ShineLabLineJsonReader
{
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private bool _overflowed;

    public ShineLabFrameReadStatus TryRead(
        out byte[] rawBytes,
        out byte[] jsonBytes,
        out string? error)
    {
        rawBytes = Array.Empty<byte>();
        jsonBytes = Array.Empty<byte>();
        error = null;

        if (_overflowed)
        {
            _overflowed = false;
            _buffer.Clear();
            error = $"ShineLab line buffer exceeded {ShineLabWireCodec.MaxLineBytes} bytes.";
            return ShineLabFrameReadStatus.InvalidFrame;
        }

        while (true)
        {
            var span = _buffer.WrittenSpan;
            var newline = span.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (span.Length > ShineLabWireCodec.MaxLineBytes + 2)
                {
                    _buffer.Clear();
                    error = $"ShineLab line exceeds {ShineLabWireCodec.MaxLineBytes} bytes.";
                    return ShineLabFrameReadStatus.InvalidFrame;
                }

                return ShineLabFrameReadStatus.NeedMoreData;
            }

            var lineLength = newline;
            if (lineLength > 0 && span[lineLength - 1] == (byte)'\r')
                lineLength--;

            var line = span[..lineLength].ToArray();
            var rawLength = newline + 1;
            var raw = span[..rawLength].ToArray();
            DiscardPrefix(rawLength);

            if (line.Length == 0)
                continue;

            if (line.Length > ShineLabWireCodec.MaxLineBytes)
            {
                error = $"ShineLab line exceeds {ShineLabWireCodec.MaxLineBytes} bytes.";
                return ShineLabFrameReadStatus.InvalidFrame;
            }

            rawBytes = raw;
            jsonBytes = line;
            return ShineLabFrameReadStatus.FrameReady;
        }
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;

        var maxBuffered = (ShineLabWireCodec.MaxLineBytes + 2) * 2;
        if (_buffer.WrittenCount + bytes.Length > maxBuffered)
        {
            _buffer.Clear();
            _overflowed = true;
            return;
        }

        _buffer.Write(bytes);
    }

    public void Reset()
    {
        _buffer.Clear();
        _overflowed = false;
    }

    private void DiscardPrefix(int count)
    {
        var remaining = _buffer.WrittenSpan[count..].ToArray();
        _buffer.Clear();
        _buffer.Write(remaining);
    }
}

/// <summary>
/// Detects the first wire format on a connection and then delegates to a
/// bounded reader for that format.  Once selected, a connection cannot switch
/// between LF JSON and 55AA frames.
/// </summary>
public sealed class ShineLabWireReader
{
    private readonly ArrayBufferWriter<byte> _pending = new();
    private ShineLabTcpFrameReader? _nativeReader;
    private ShineLabLineJsonReader? _lineReader;
    private bool _detectionOverflowed;

    public ShineLabWireFormat Format { get; private set; }

    public ShineLabFrameReadStatus TryRead(
        out ShineLabWireFrame frame,
        out string? error)
    {
        frame = default;
        error = null;

        if (Format == ShineLabWireFormat.Unknown)
        {
            if (_detectionOverflowed)
            {
                _detectionOverflowed = false;
                _pending.Clear();
                error = $"ShineLab wire format detection buffer exceeded {ShineLabWireCodec.MaxDetectionBytes} bytes.";
                return ShineLabFrameReadStatus.InvalidFrame;
            }

            var detection = DetectFormat();
            if (detection == ShineLabFrameReadStatus.InvalidFrame)
            {
                error = "Unable to detect a supported ShineLab wire format.";
                return detection;
            }
            if (Format == ShineLabWireFormat.Unknown)
                return detection;
        }

        if (Format == ShineLabWireFormat.Native55Aa)
        {
            var status = _nativeReader!.TryRead(out var nativeFrame, out error);
            if (status == ShineLabFrameReadStatus.FrameReady)
            {
                var lengthField = (nativeFrame[2] << 8) | nativeFrame[3];
                var jsonLength = lengthField - ShineLabTcpFrameCodec.TerminatorLength;
                frame = new(
                    ShineLabWireFormat.Native55Aa,
                    nativeFrame,
                    nativeFrame.AsSpan(ShineLabTcpFrameCodec.HeaderLength, jsonLength).ToArray());
            }

            return status;
        }

        var lineStatus = _lineReader!.TryRead(out var raw, out var json, out error);
        if (lineStatus == ShineLabFrameReadStatus.FrameReady)
            frame = new(ShineLabWireFormat.LineJson, raw, json);
        return lineStatus;
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;

        if (Format == ShineLabWireFormat.Native55Aa)
        {
            _nativeReader!.Append(bytes);
            return;
        }

        if (Format == ShineLabWireFormat.LineJson)
        {
            _lineReader!.Append(bytes);
            return;
        }

        if (_pending.WrittenCount + bytes.Length > ShineLabWireCodec.MaxDetectionBytes)
        {
            _pending.Clear();
            _detectionOverflowed = true;
            return;
        }

        _pending.Write(bytes);
    }

    public void Reset()
    {
        _pending.Clear();
        _nativeReader?.Reset();
        _lineReader?.Reset();
        _nativeReader = null;
        _lineReader = null;
        _detectionOverflowed = false;
        Format = ShineLabWireFormat.Unknown;
    }

    private ShineLabFrameReadStatus DetectFormat()
    {
        var span = _pending.WrittenSpan;
        var index = 0;
        while (index < span.Length && IsAsciiWhitespace(span[index])) index++;

        if (index >= span.Length)
            return ShineLabFrameReadStatus.NeedMoreData;

        if (span[index] == ShineLabTcpFrameCodec.HeaderFirst)
        {
            if (index + 1 >= span.Length)
                return ShineLabFrameReadStatus.NeedMoreData;

            if (span[index + 1] == ShineLabTcpFrameCodec.HeaderSecond)
            {
                var bytes = span[index..].ToArray();
                _pending.Clear();
                _nativeReader = new ShineLabTcpFrameReader();
                _nativeReader.Append(bytes);
                Format = ShineLabWireFormat.Native55Aa;
                return ShineLabFrameReadStatus.NeedMoreData;
            }
        }

        if (span[index] is (byte)'{' or (byte)'[')
        {
            var bytes = span[index..].ToArray();
            _pending.Clear();
            _lineReader = new ShineLabLineJsonReader();
            _lineReader.Append(bytes);
            Format = ShineLabWireFormat.LineJson;
            return ShineLabFrameReadStatus.NeedMoreData;
        }

        _pending.Clear();
        return ShineLabFrameReadStatus.InvalidFrame;
    }

    private static bool IsAsciiWhitespace(byte value) => value is 0x09 or 0x0A or 0x0D or 0x20;
}
