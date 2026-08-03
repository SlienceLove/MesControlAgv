using System.Text;
using MesControlAgv.Adapter.Services;

namespace MesControlAgv.Adapter.Tests;

public sealed class TcpAgvProtocolTests
{
    [Fact]
    public void Request_header_matches_vendor_relocation_example()
    {
        var payload = Encoding.UTF8.GetBytes("{\"x\":10.0,\"y\":3.0,\"angle\":0}");

        var packet = AgvTcpProtocol.CreatePacket(2002, payload);

        Assert.Equal("5A0100010000001C07D2000000000000", Convert.ToHexString(packet[..16]));
        Assert.Equal(payload, packet[16..]);
    }

    [Fact]
    public async Task Reader_accepts_a_split_header_and_payload()
    {
        var expectedPayload = Encoding.UTF8.GetBytes("{\"ret_code\":0}");
        var packet = AgvTcpProtocol.CreatePacket(11060, expectedPayload);
        await using var stream = new SlowReadStream(packet, 1);

        var actual = await AgvTcpProtocol.ReadPacketAsync(stream, 1024, CancellationToken.None);

        Assert.Equal((ushort)11060, actual.ApiId);
        Assert.Equal(expectedPayload, actual.Payload);
    }
}

internal sealed class SlowReadStream(byte[] data, int chunkSize) : Stream
{
    private int _offset;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _offset; set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset >= data.Length) return ValueTask.FromResult(0);
        var count = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _offset);
        data.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return ValueTask.FromResult(count);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
