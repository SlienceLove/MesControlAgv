using System.Text;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabTcpFrameCodecTests
{
    [Fact]
    public void Encode_uses_big_endian_length_and_trailing_nul_without_newline()
    {
        const string json = "{\"strMethod\":\"Certification\",\"body\":{\"站点\":\"A\"}}";

        var frame = ShineLabTcpFrameCodec.EncodeJson(json);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var lengthField = jsonBytes.Length + 1;

        Assert.Equal(ShineLabTcpFrameCodec.HeaderFirst, frame[0]);
        Assert.Equal(ShineLabTcpFrameCodec.HeaderSecond, frame[1]);
        Assert.Equal((byte)(lengthField >> 8), frame[2]);
        Assert.Equal((byte)lengthField, frame[3]);
        Assert.Equal(0, frame[^1]);
        Assert.Equal(jsonBytes.Length + 5, frame.Length);
        Assert.DoesNotContain((byte)'\n', frame);
        Assert.True(ShineLabTcpFrameCodec.TryDecodeJson(frame, out var decoded, out var error), error);
        Assert.Equal(json, decoded);
    }

    [Fact]
    public void Reader_handles_header_payload_and_terminator_split_across_reads()
    {
        var frame = ShineLabTcpFrameCodec.EncodeJson("{\"strID\":\"split\",\"body\":{}} ".TrimEnd());
        var reader = new ShineLabTcpFrameReader();

        for (var index = 0; index < frame.Length; index++)
        {
            reader.Append(frame.AsSpan(index, 1));
            var result = reader.TryRead(out var actual, out var error);
            if (index < frame.Length - 1)
            {
                Assert.Equal(ShineLabFrameReadStatus.NeedMoreData, result);
                Assert.Null(error);
            }
            else
            {
                Assert.Equal(ShineLabFrameReadStatus.FrameReady, result);
                Assert.Equal(frame, actual);
            }
        }
    }

    [Fact]
    public void Reader_returns_multiple_frames_from_one_append()
    {
        var first = ShineLabTcpFrameCodec.EncodeJson("{\"n\":1}");
        var second = ShineLabTcpFrameCodec.EncodeJson("{\"n\":2}");
        var combined = first.Concat(second).ToArray();
        var reader = new ShineLabTcpFrameReader();
        reader.Append(combined);

        Assert.Equal(ShineLabFrameReadStatus.FrameReady, reader.TryRead(out var actualFirst, out var firstError));
        Assert.Null(firstError);
        Assert.Equal(first, actualFirst);
        Assert.Equal(ShineLabFrameReadStatus.FrameReady, reader.TryRead(out var actualSecond, out var secondError));
        Assert.Null(secondError);
        Assert.Equal(second, actualSecond);
        Assert.Equal(ShineLabFrameReadStatus.NeedMoreData, reader.TryRead(out _, out _));
    }

    [Fact]
    public void Reader_discards_noise_and_recovers_after_bad_terminator()
    {
        var valid = ShineLabTcpFrameCodec.EncodeJson("{\"ok\":true}");
        var invalid = (byte[])valid.Clone();
        invalid[^1] = 0x7f;
        var reader = new ShineLabTcpFrameReader();
        reader.Append(new byte[] { 0x01, 0x02 });
        reader.Append(invalid);
        Assert.Equal(ShineLabFrameReadStatus.InvalidFrame, reader.TryRead(out _, out var error));
        Assert.Contains("NUL", error, StringComparison.OrdinalIgnoreCase);

        reader.Append(new byte[] { 0x10, 0x11 });
        reader.Append(valid);
        Assert.Equal(ShineLabFrameReadStatus.FrameReady, reader.TryRead(out var recovered, out var recoveredError));
        Assert.Null(recoveredError);
        Assert.Equal(valid, recovered);
    }

    [Fact]
    public void Decoder_rejects_invalid_utf8_and_missing_nul()
    {
        var invalidUtf8 = new byte[] { 0x55, 0xAA, 0x00, 0x02, 0xC3, 0x00 };
        Assert.False(ShineLabTcpFrameCodec.TryDecodeJson(invalidUtf8, out _, out var utf8Error));
        Assert.Contains("UTF-8", utf8Error, StringComparison.OrdinalIgnoreCase);

        var missingNul = ShineLabTcpFrameCodec.EncodeJson("{}");
        missingNul[^1] = 0x01;
        Assert.False(ShineLabTcpFrameCodec.TryDecodeJson(missingNul, out _, out var nulError));
        Assert.Contains("NUL", nulError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reader_does_not_treat_legacy_newline_json_as_a_native_frame()
    {
        var reader = new ShineLabTcpFrameReader();
        reader.Append(Encoding.UTF8.GetBytes("{\"strMethod\":\"Certification\"}\n"));

        Assert.Equal(ShineLabFrameReadStatus.NeedMoreData, reader.TryRead(out _, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Encoder_rejects_json_larger_than_uint16_length_field()
    {
        var json = new string('x', ShineLabTcpFrameCodec.MaxJsonBytes + 1);
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => ShineLabTcpFrameCodec.EncodeJson(json));
        Assert.Contains("UTF-8 bytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Line_reader_handles_split_heart_and_multiple_json_lines()
    {
        const string first = "{\"strID\":\"h1\",\"strMethod\":\"Heart\",\"equipmentCode\":\"STN61_01\",\"body\":{\"type\":\"ping\"}}";
        const string second = "{\"strID\":\"b1\",\"strMethod\":\"BindModule\",\"equipmentCode\":\"STN61_01\",\"strCode\":\"\",\"body\":{\"chan\":\"\"}}";
        var bytes = Encoding.UTF8.GetBytes(first + "\n" + second + "\n");
        var reader = new ShineLabWireReader();

        for (var index = 0; index < bytes.Length; index++)
            reader.Append(bytes.AsSpan(index, 1));

        Assert.Equal(ShineLabFrameReadStatus.FrameReady, reader.TryRead(out var firstFrame, out var firstError));
        Assert.Null(firstError);
        Assert.Equal(ShineLabWireFormat.LineJson, firstFrame.Format);
        Assert.Equal(first, Encoding.UTF8.GetString(firstFrame.JsonBytes));
        Assert.Equal(ShineLabFrameReadStatus.FrameReady, reader.TryRead(out var secondFrame, out var secondError));
        Assert.Null(secondError);
        Assert.Equal(second, Encoding.UTF8.GetString(secondFrame.JsonBytes));
        Assert.Equal(ShineLabFrameReadStatus.NeedMoreData, reader.TryRead(out _, out _));
    }

    [Fact]
    public void Line_codec_appends_lf_and_strips_optional_cr_before_decode()
    {
        const string json = "{\"strMethod\":\"Heart\"}";
        var encoded = ShineLabWireCodec.EncodeJson(json, ShineLabWireFormat.LineJson);
        Assert.Equal((byte)'\n', encoded[^1]);

        var reader = new ShineLabWireReader();
        reader.Append(encoded[..^1]);
        reader.Append(new byte[] { (byte)'\r', (byte)'\n' });
        Assert.Equal(ShineLabFrameReadStatus.FrameReady, reader.TryRead(out var frame, out var error));
        Assert.Null(error);
        Assert.True(ShineLabWireCodec.TryDecodeJson(frame, out var decoded, out var decodeError), decodeError);
        Assert.Equal(json, decoded);
    }

    [Fact]
    public void Wire_reader_keeps_native_55aa_compatibility()
    {
        var native = ShineLabTcpFrameCodec.EncodeJson("{\"strMethod\":\"Certification\"}");
        var reader = new ShineLabWireReader();
        reader.Append(native);

        Assert.Equal(ShineLabFrameReadStatus.FrameReady, reader.TryRead(out var frame, out var error));
        Assert.Null(error);
        Assert.Equal(ShineLabWireFormat.Native55Aa, reader.Format);
        Assert.True(ShineLabWireCodec.TryDecodeJson(frame, out var json, out var decodeError), decodeError);
        Assert.Equal("{\"strMethod\":\"Certification\"}", json);
    }

    [Fact]
    public void Unknown_wire_bytes_are_rejected_without_emitting_data()
    {
        var reader = new ShineLabWireReader();
        reader.Append(new byte[] { 0x01, 0x02, 0x03 });

        Assert.Equal(ShineLabFrameReadStatus.InvalidFrame, reader.TryRead(out _, out var error));
        Assert.Contains("format", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ShineLabWireFormat.Unknown, reader.Format);
    }

    [Fact]
    public void Line_reader_rejects_an_oversized_line_and_recovers_on_the_next_frame()
    {
        var reader = new ShineLabWireReader();
        var oversized = Encoding.UTF8.GetBytes("{" + new string('x', ShineLabWireCodec.MaxLineBytes) + "\n");
        reader.Append(oversized);

        Assert.Equal(ShineLabFrameReadStatus.InvalidFrame, reader.TryRead(out _, out var error));
        Assert.Contains("exceed", error, StringComparison.OrdinalIgnoreCase);

        reader.Append(Encoding.UTF8.GetBytes("{\"strMethod\":\"Heart\"}\n"));
        Assert.Equal(ShineLabFrameReadStatus.FrameReady, reader.TryRead(out var frame, out var recoveryError));
        Assert.Null(recoveryError);
        Assert.Equal(ShineLabWireFormat.LineJson, frame.Format);
    }

    [Fact]
    public void Wire_reader_rejects_an_unbounded_detection_prefix()
    {
        var reader = new ShineLabWireReader();
        reader.Append(new byte[ShineLabWireCodec.MaxDetectionBytes + 1]);

        Assert.Equal(ShineLabFrameReadStatus.InvalidFrame, reader.TryRead(out _, out var error));
        Assert.Contains("detection", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ShineLabWireFormat.Unknown, reader.Format);
    }
}
