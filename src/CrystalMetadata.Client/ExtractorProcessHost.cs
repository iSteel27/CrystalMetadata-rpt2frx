using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CrystalMetadata.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrystalMetadata.Client;

/// <summary>A worker slot: one pipe name and (unless attached) one supervised server process.</summary>
public sealed class WorkerSlot
{
    internal WorkerSlot(int index, string pipeName)
    {
        Index = index;
        PipeName = pipeName;
    }

    public int Index { get; }
    public string PipeName { get; }
    public int Restarts { get; internal set; }
    internal Process? Process { get; set; }
    internal bool NeedsRestart { get; set; }

    /// <summary>Set while the host itself is stopping the process, so its exit is not reported as a crash.</summary>
    internal volatile bool Stopping;

    public bool IsRunning => Process is { HasExited: false };
}

/// <summary>
/// Spawns and supervises <c>CrystalMetadata.PipeServer.exe</c> processes (one per worker), waits for their READY line,
/// restarts them on crash / recycle / timeout, and hands out slots to callers. In attach mode it only hands out the single
/// configured pipe name and never spawns anything.
/// </summary>
public sealed class ExtractorProcessHost : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly ILogger _logger;
    private readonly List<WorkerSlot> _slots = new();
    private readonly Channel<WorkerSlot> _available;
    private readonly SemaphoreSlim _spawnLock = new(1, 1);
    private bool _disposed;

    public ExtractorProcessHost(ClientOptions options, ILogger<ExtractorProcessHost>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<ExtractorProcessHost>.Instance;

        var count = options.AttachToExistingServer ? 1 : Math.Max(1, options.WorkerCount);
        _available = Channel.CreateUnbounded<WorkerSlot>();
        for (var i = 0; i < count; i++)
        {
            var pipe = options.AttachToExistingServer ? options.PipeName : options.PipeName + "." + i;
            var slot = new WorkerSlot(i, pipe);
            _slots.Add(slot);
            _available.Writer.TryWrite(slot);
        }
    }

    public IReadOnlyList<WorkerSlot> Slots => _slots;

    public int WorkerCount => _slots.Count;

    /// <summary>Takes a free slot (waiting when all are busy) and makes sure its server is running.</summary>
    public async Task<WorkerSlot> AcquireAsync(CancellationToken ct)
    {
        var slot = await _available.Reader.ReadAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureRunningAsync(slot, ct).ConfigureAwait(false);
            return slot;
        }
        catch
        {
            _available.Writer.TryWrite(slot);
            throw;
        }
    }

    public void Release(WorkerSlot slot)
    {
        if (!_disposed)
        {
            _available.Writer.TryWrite(slot);
        }
    }

    /// <summary>Marks the slot so the next acquire restarts its process (used after transport failures / recycle notices).</summary>
    public void MarkForRestart(WorkerSlot slot, string reason)
    {
        if (_options.AttachToExistingServer)
        {
            _logger.LogWarning("Attached server on {Pipe} reported: {Reason}; cannot restart it from the client", slot.PipeName, reason);
            return;
        }

        _logger.LogWarning("Worker {Index} ({Pipe}) will be restarted: {Reason}", slot.Index, slot.PipeName, reason);
        slot.NeedsRestart = true;
    }

    /// <summary>Kills the slot's process immediately (deadline exceeded) and marks it for restart.</summary>
    public void Kill(WorkerSlot slot, string reason)
    {
        if (_options.AttachToExistingServer)
        {
            MarkForRestart(slot, reason);
            return;
        }

        try
        {
            if (slot.Process is { HasExited: false } p)
            {
                _logger.LogWarning("Killing worker {Index} (pid {Pid}): {Reason}", slot.Index, p.Id, reason);
                slot.Stopping = true;
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Kill failed for worker {Index}", slot.Index);
        }

        slot.NeedsRestart = true;
    }

    public async Task EnsureRunningAsync(WorkerSlot slot, CancellationToken ct)
    {
        if (_options.AttachToExistingServer)
        {
            return;
        }

        if (slot.IsRunning && !slot.NeedsRestart)
        {
            return;
        }

        await _spawnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (slot.IsRunning && !slot.NeedsRestart)
            {
                return;
            }

            StopProcess(slot);
            await StartProcessAsync(slot, ct).ConfigureAwait(false);
        }
        finally
        {
            _spawnLock.Release();
        }
    }

    private async Task StartProcessAsync(WorkerSlot slot, CancellationToken ct)
    {
        var exe = ResolveServerExe();
        var args = "--pipe \"" + slot.PipeName + "\" --max-reports " + _options.MaxReportsPerProcess + " --timeout " + _options.RequestTimeoutMs;
        if (!string.IsNullOrWhiteSpace(_options.ServerExtraArgs))
        {
            args += " " + _options.ServerExtraArgs;
        }

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.Trim() == ProtocolConstants.ReadyLine)
            {
                ready.TrySetResult(true);
            }
            else
            {
                _logger.LogDebug("[worker {Index} out] {Line}", slot.Index, e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) _logger.LogDebug("[worker {Index}] {Line}", slot.Index, e.Data);
        };
        process.Exited += (_, _) =>
        {
            var code = SafeExitCode(process);
            if (slot.Stopping)
            {
                _logger.LogDebug("Worker {Index} stopped by host (code {Code})", slot.Index, code);
            }
            else if (code == ProtocolConstants.RecycleExitCode)
            {
                _logger.LogInformation("Worker {Index} exited for recycle", slot.Index);
            }
            else if (code != 0)
            {
                _logger.LogWarning("Worker {Index} exited with code {Code}", slot.Index, code);
            }

            ready.TrySetResult(false);
            slot.NeedsRestart = true;
        };

        _logger.LogInformation("Starting worker {Index}: {Exe} {Args}", slot.Index, exe, args);
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start " + exe);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        slot.Process = process;
        slot.Stopping = false;
        slot.NeedsRestart = false;
        slot.Restarts++;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ServerStartTimeoutMs);
        try
        {
            var ok = await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (!ok)
            {
                var code = SafeExitCode(process);
                throw new InvalidOperationException(code switch
                {
                    ProtocolConstants.RuntimeMissingExitCode => "Extractor exited with code 3: the SAP Crystal Reports runtime is not installed (or bitness mismatch).",
                    // 0xE0434352 = an unhandled .NET exception before the server could report anything
                    -532462766 => "Extractor crashed at startup (code -532462766, a .NET error). Usual cause: the extractor build is incomplete or mixed with old files. Close any open PipeServer window and use Build > Rebuild Solution. To see the exact error run: " + exe + " --extract <file.rpt>",
                    _ => "Extractor exited during startup with code " + code + "."
                });
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            StopProcess(slot);
            throw new TimeoutException("Extractor on " + slot.PipeName + " did not report READY within " + _options.ServerStartTimeoutMs + " ms.");
        }

        _logger.LogInformation("WorkerRestarted index={Index} pid={Pid} pipe={Pipe}", slot.Index, process.Id, slot.PipeName);
    }

    private void StopProcess(WorkerSlot slot)
    {
        var p = slot.Process;
        slot.Process = null;
        if (p == null) return;
        slot.Stopping = true;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping worker {Index} failed", slot.Index);
        }
        finally
        {
            p.Dispose();
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.HasExited ? p.ExitCode : -1; } catch { return -1; }
    }

    private string ResolveServerExe()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.ServerExePath))
        {
            candidates.Add(Path.GetFullPath(_options.ServerExePath));
        }

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "CrystalMetadata.PipeServer.exe"));
        candidates.Add(Path.Combine(baseDir, "server", "CrystalMetadata.PipeServer.exe"));
        candidates.Add(Path.Combine(baseDir, "..", "CrystalMetadata.PipeServer", "CrystalMetadata.PipeServer.exe"));
        // dev-tree convenience: src/CrystalMetadata.Console/bin/<cfg>/net10.0 → src/CrystalMetadata.PipeServer/bin/<cfg>/net48
        // the configuration this app was built in first, then the other one as a fallback
        var cfg = new DirectoryInfo(baseDir).Parent?.Name ?? "Release";
        foreach (var name in new[] { cfg, "Release", "Debug" }.Distinct())
        {
            candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "CrystalMetadata.PipeServer", "bin", name, "net48", "CrystalMetadata.PipeServer.exe")));
        }

        var incomplete = new List<string>();
        foreach (var c in candidates)
        {
            if (!File.Exists(c)) continue;

            // The server cannot start without its .exe.config (assembly binding redirects). A build that was interrupted
            // — typically because an old server window still locked the files — leaves the .exe without it.
            if (!File.Exists(c + ".config"))
            {
                incomplete.Add(c);
                continue;
            }

            if (incomplete.Count > 0)
            {
                _logger.LogWarning("Skipping incomplete extractor build {Skipped} (missing .exe.config); using {Used}. Rebuild the solution to refresh it.", incomplete[0], c);
            }

            return c;
        }

        if (incomplete.Count > 0)
        {
            throw new FileNotFoundException("The extractor build is incomplete: " + incomplete[0] + ".config is missing. Close any running CrystalMetadata.PipeServer window (it locks the files), then rebuild the whole solution.");
        }

        throw new FileNotFoundException("CrystalMetadata.PipeServer.exe not found. Build the solution, or set Client:ServerExePath. Tried: " + string.Join("; ", candidates));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _available.Writer.TryComplete();
        foreach (var slot in _slots)
        {
            StopProcess(slot);
        }

        _spawnLock.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
