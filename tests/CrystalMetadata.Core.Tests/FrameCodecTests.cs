using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Protocol;
using Xunit;

namespace CrystalMetadata.Core.Tests;

public class FrameCodecTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(1_000_001)]
    public async Task WriteThenRead_RoundTrips(int size)
    {
        var payload = new byte[size];
        new Random(size).NextBytes(payload);
        using var ms = new MemoryStream();
        await FrameCodec.WriteFrameAsync(ms, payload, CancellationToken.None);
        Assert.Equal(size + FrameCodec.HeaderSize, ms.Length);

        ms.Position = 0;
        var read = await FrameCodec.ReadFrameAsync(ms, ProtocolConstants.DefaultMaxFrameBytes, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(payload, read);

        // clean end of stream at a frame boundary -> null
        Assert.Null(await FrameCodec.ReadFrameAsync(ms, ProtocolConstants.DefaultMaxFrameBytes, CancellationToken.None));
    }

    [Fact]
    public async Task Header_IsLittleEndian()
    {
        using var ms = new MemoryStream();
        await FrameCodec.WriteFrameAsync(ms, new byte[0x01020304 & 0xFF], CancellationToken.None);
        var bytes = ms.ToArray();
        Assert.Equal(4, bytes[0]);
        Assert.Equal(0, bytes[1]);
    }

    [Fact]
    public async Task Oversize_ThrowsFrameTooLarge()
    {
        using var ms = new MemoryStream();
        await FrameCodec.WriteFrameAsync(ms, new byte[100], CancellationToken.None);
        ms.Position = 0;
        var ex = await Assert.ThrowsAsync<FrameTooLargeException>(() => FrameCodec.ReadFrameAsync(ms, 50, CancellationToken.None));
        Assert.Equal(100, ex.Announced);
        Assert.Equal(50, ex.Max);
    }

    [Fact]
    public async Task TruncatedPayload_ThrowsEndOfStream()
    {
        using var ms = new MemoryStream();
        await FrameCodec.WriteFrameAsync(ms, new byte[100], CancellationToken.None);
        var truncated = new MemoryStream(ms.ToArray(), 0, 60);
        await Assert.ThrowsAsync<EndOfStreamException>(() => FrameCodec.ReadFrameAsync(truncated, 1000, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedHeader_ThrowsEndOfStream()
    {
        var truncated = new MemoryStream(new byte[] { 1, 0 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => FrameCodec.ReadFrameAsync(truncated, 1000, CancellationToken.None));
    }

    [Fact]
    public async Task PartialReads_AreReassembled()
    {
        using var ms = new MemoryStream();
        var payload = new byte[10_000];
        new Random(7).NextBytes(payload);
        await FrameCodec.WriteFrameAsync(ms, payload, CancellationToken.None);
        var drip = new DripStream(ms.ToArray(), 3);
        var read = await FrameCodec.ReadFrameAsync(drip, 1_000_000, CancellationToken.None);
        Assert.Equal(payload, read);
    }

    [Fact]
    public void LegacyMinimalRequest_IsAccepted()
    {
        var request = MetadataJson.Deserialize<PipeRequest>("{ \"reportPath\": \"C:\\\\Reports\\\\SalesRegister.rpt\" }")!;
        Assert.Equal(@"C:\Reports\SalesRegister.rpt", request.ReportPath);
        Assert.Equal(PipeCommands.Extract, request.Command);
        Assert.Equal(ProtocolConstants.Version, request.ProtocolVersion);
        Assert.False(string.IsNullOrEmpty(request.RequestId));
        Assert.Null(request.Options);
    }

    [Fact]
    public void Response_RoundTrips()
    {
        var response = PipeResponse.Failure("r1", ErrorCodes.LoadFailed, "boom", "detail", new ServerInfo { ProcessId = 42, Bitness = "x64" });
        var back = MetadataJson.Deserialize<PipeResponse>(MetadataJson.SerializeToBytes(response))!;
        Assert.False(back.Success);
        Assert.Equal(ErrorCodes.LoadFailed, back.Error!.Code);
        Assert.Equal(42, back.Server!.ProcessId);
        Assert.Null(back.Metadata);
    }

    /// <summary>Stream that returns at most N bytes per read, to exercise reassembly.</summary>
    private sealed class DripStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _pos;

        public DripStream(byte[] data, int chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, _chunk), _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
