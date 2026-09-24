using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Protocol;

namespace CrystalMetadata.Client.Tests;

/// <summary>
/// In-process stand-in for CrystalMetadata.PipeServer: same framing, same envelopes, scripted behaviour.
/// Behaviours: normal response, disconnect without answering, garbage frame, delayed response.
/// </summary>
public sealed class FakePipeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakePipeServer(string pipeName, Func<PipeRequest, Task<FakeBehaviour>> handler)
    {
        PipeName = pipeName;
        Handler = handler;
        _loop = Task.Run(LoopAsync);
    }

    public string PipeName { get; }
    public Func<PipeRequest, Task<FakeBehaviour>> Handler { get; }
    public int Connections;

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token);
                Interlocked.Increment(ref Connections);
                var frame = await FrameCodec.ReadFrameAsync(pipe, ProtocolConstants.DefaultMaxFrameBytes, _cts.Token);
                if (frame == null) continue;
                var request = MetadataJson.Deserialize<PipeRequest>(frame)!;
                var behaviour = await Handler(request);
                switch (behaviour.Kind)
                {
                    case FakeKind.Respond:
                        behaviour.Response!.RequestId = request.RequestId;
                        await FrameCodec.WriteFrameAsync(pipe, MetadataJson.SerializeToBytes(behaviour.Response), _cts.Token);
                        break;
                    case FakeKind.Disconnect:
                        break;
                    case FakeKind.Garbage:
                        await FrameCodec.WriteFrameAsync(pipe, new byte[] { 1, 2, 3 }, _cts.Token);
                        break;
                    case FakeKind.HalfFrame:
                        await pipe.WriteAsync(new byte[] { 200, 0, 0, 0, 1, 2 }, _cts.Token);
                        break;
                    case FakeKind.Hang:
                        await Task.Delay(behaviour.DelayMs, _cts.Token);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // client closed early
            }
            finally
            {
                if (pipe != null)
                {
                    try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
                    await pipe.DisposeAsync();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}

public enum FakeKind
{
    Respond,
    Disconnect,
    Garbage,
    HalfFrame,
    Hang
}

public sealed class FakeBehaviour
{
    public FakeKind Kind { get; init; }
    public PipeResponse? Response { get; init; }
    public int DelayMs { get; init; }

    public static FakeBehaviour Respond(PipeResponse response) => new() { Kind = FakeKind.Respond, Response = response };
    public static FakeBehaviour Disconnect() => new() { Kind = FakeKind.Disconnect };
    public static FakeBehaviour Garbage() => new() { Kind = FakeKind.Garbage };
    public static FakeBehaviour HalfFrame() => new() { Kind = FakeKind.HalfFrame };
    public static FakeBehaviour Hang(int ms) => new() { Kind = FakeKind.Hang, DelayMs = ms };
}
