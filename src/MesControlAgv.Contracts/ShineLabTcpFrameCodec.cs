using System.Buffers;
using System.Text;
using System.Text.Json;

namespace MesControlAgv.Contracts;

/// <summary>
/// Wire codec used by ShineLab's native TCP transport.  The transport is not
/// line-delimited JSON: it uses a 55 AA header, a two-byte big-endian length
/// (JSON byte count plus the trailing NUL), the JSON bytes, and one NUL byte.
/// </summary>
public static class ShineLabTcpFrameCodec
{
    public const byte HeaderFirst = 0x55;
    public const byte HeaderSecond = 0xAA;
    public const int HeaderLength = 4;
    public const int TerminatorLength = 1;
    public const int MaxLengthField = ushort.MaxValue;
    public const int MaxJsonBytes = MaxLengthField - TerminatorLength;
    public const int MaxFrameBytes = HeaderLength + MaxLengthField;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] EncodeJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var jsonBytes = StrictUtf8.GetBytes(json);
        if (jsonBytes.Length > MaxJsonBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(json),
                jsonBytes.Length,
                $"ShineLab JSON payload cannot exceed {MaxJsonBytes} UTF-8 bytes.");
        }

        var lengthField = jsonBytes.Length + TerminatorLength;
        var frame = new byte[HeaderLength + lengthField];
        frame[0] = HeaderFirst;
        frame[1] = HeaderSecond;
        frame[2] = (byte)(lengthField >> 8);
        frame[3] = (byte)lengthField;
        jsonBytes.CopyTo(frame, HeaderLength);
        // Arrays are zero-initialized; assign explicitly to make the wire
        // contract obvious and resilient to future buffer changes.
        frame[^1] = 0;
        return frame;
    }

    public static byte[] Encode<T>(T value, JsonSerializerOptions? options = null) =>
        EncodeJson(JsonSerializer.Serialize(value, options));

    public static bool TryDecodeJson(
        ReadOnlySpan<byte> frame,
        out string json,
        out string error)
    {
        json = string.Empty;
        if (frame.Length < HeaderLength + TerminatorLength)
        {
            error = "Frame is shorter than the ShineLab header and terminator.";
            return false;
        }

        if (frame[0] != HeaderFirst || frame[1] != HeaderSecond)
        {
            error = "Frame header is not 55 AA.";
            return false;
        }

        var lengthField = (frame[2] << 8) | frame[3];
        if (lengthField < TerminatorLength)
        {
            error = "Frame length must include the trailing NUL.";
            return false;
        }

        var expectedLength = HeaderLength + lengthField;
        if (frame.Length != expectedLength)
        {
            error = $"Frame length is {frame.Length}, expected {expectedLength}.";
            return false;
        }

        if (frame[^1] != 0)
        {
            error = "Frame is missing its trailing NUL.";
            return false;
        }

        try
        {
            json = StrictUtf8.GetString(frame.Slice(HeaderLength, lengthField - TerminatorLength));
            error = string.Empty;
            return true;
        }
        catch (DecoderFallbackException exception)
        {
            error = $"Frame payload is not valid UTF-8: {exception.Message}";
            return false;
        }
    }
}

public enum ShineLabFrameReadStatus
{
    NeedMoreData,
    FrameReady,
    InvalidFrame
}

/// <summary>
/// Incremental, bounded parser for the native ShineLab frame.  It copies a
/// completed frame so callers may append more network data immediately.
/// </summary>
public sealed class ShineLabTcpFrameReader
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public ShineLabFrameReadStatus TryRead(
        out byte[] frame,
        out string? error)
    {
        frame = Array.Empty<byte>();
        error = null;

        var span = _buffer.WrittenSpan;
        var headerIndex = FindHeader(span);
        if (headerIndex < 0)
        {
            PreservePossibleHeaderPrefix(span);
            return ShineLabFrameReadStatus.NeedMoreData;
        }

        if (headerIndex > 0)
        {
            DiscardPrefix(headerIndex);
            span = _buffer.WrittenSpan;
        }

        if (span.Length < ShineLabTcpFrameCodec.HeaderLength)
            return ShineLabFrameReadStatus.NeedMoreData;

        var lengthField = (span[2] << 8) | span[3];
        if (lengthField < ShineLabTcpFrameCodec.TerminatorLength)
        {
            DiscardPrefix(1);
            error = $"Invalid ShineLab frame length {lengthField}.";
            return ShineLabFrameReadStatus.InvalidFrame;
        }

        var totalLength = ShineLabTcpFrameCodec.HeaderLength + lengthField;
        if (totalLength > ShineLabTcpFrameCodec.MaxFrameBytes)
        {
            DiscardPrefix(1);
            error = $"ShineLab frame length {lengthField} exceeds the maximum.";
            return ShineLabFrameReadStatus.InvalidFrame;
        }

        if (span.Length < totalLength)
            return ShineLabFrameReadStatus.NeedMoreData;

        if (span[totalLength - 1] != 0)
        {
            DiscardPrefix(1);
            error = "ShineLab frame does not end with NUL.";
            return ShineLabFrameReadStatus.InvalidFrame;
        }

        frame = span[..totalLength].ToArray();
        DiscardPrefix(totalLength);
        return ShineLabFrameReadStatus.FrameReady;
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;

        var maxBuffered = ShineLabTcpFrameCodec.MaxFrameBytes * 2;
        if (_buffer.WrittenCount + bytes.Length > maxBuffered)
        {
            // Keep only the newest bounded window.  The next TryRead call will
            // resynchronise on 55 AA; callers can decide whether to close the
            // connection after observing the invalid-frame result.
            if (bytes.Length >= maxBuffered)
            {
                _buffer.Clear();
                _buffer.Write(bytes[^maxBuffered..]);
                return;
            }

            var keep = Math.Min(maxBuffered - bytes.Length, _buffer.WrittenCount);
            if (keep > 0)
            {
                var existing = _buffer.WrittenSpan[^keep..].ToArray();
                _buffer.Clear();
                _buffer.Write(existing);
            }
            else
            {
                _buffer.Clear();
            }
        }

        _buffer.Write(bytes);
    }

    public void Reset() => _buffer.Clear();

    private static int FindHeader(ReadOnlySpan<byte> span)
    {
        for (var index = 0; index + 1 < span.Length; index++)
        {
            if (span[index] == ShineLabTcpFrameCodec.HeaderFirst &&
                span[index + 1] == ShineLabTcpFrameCodec.HeaderSecond)
                return index;
        }

        return -1;
    }

    private void PreservePossibleHeaderPrefix(ReadOnlySpan<byte> span)
    {
        if (span.Length > 0 && span[^1] == ShineLabTcpFrameCodec.HeaderFirst)
        {
            var last = new[] { ShineLabTcpFrameCodec.HeaderFirst };
            _buffer.Clear();
            _buffer.Write(last);
        }
        else
        {
            _buffer.Clear();
        }
    }

    private void DiscardPrefix(int count)
    {
        if (count <= 0) return;
        var remaining = _buffer.WrittenSpan[count..].ToArray();
        _buffer.Clear();
        _buffer.Write(remaining);
    }
}
