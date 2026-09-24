using System;
using CrystalMetadata.Core.Protocol;

namespace CrystalMetadata.Client;

/// <summary>Bound from the "Client" configuration section.</summary>
public sealed class ClientOptions
{
    /// <summary>Base pipe name. Worker <c>i</c> uses <c>{PipeName}.{i}</c> when the client spawns servers; the bare name in attach mode.</summary>
    public string PipeName { get; set; } = ProtocolConstants.DefaultPipeName;

    /// <summary>Path of CrystalMetadata.PipeServer.exe. When empty the client looks next to its own assembly and in ../CrystalMetadata.PipeServer.</summary>
    public string? ServerExePath { get; set; }

    /// <summary>Number of extractor processes to run in parallel. Default: min(4, cores/2), at least 1.</summary>
    public int WorkerCount { get; set; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

    /// <summary>Use an already running server (e.g. Windows service) on <see cref="PipeName"/>; never spawn processes.</summary>
    public bool AttachToExistingServer { get; set; }

    public int ConnectTimeoutMs { get; set; } = 10_000;
    public int RequestTimeoutMs { get; set; } = ProtocolConstants.DefaultTimeoutMs;
    public int ServerStartTimeoutMs { get; set; } = 60_000;
    public int MaxReportsPerProcess { get; set; } = 200;
    public int MaxFrameBytes { get; set; } = ProtocolConstants.DefaultMaxFrameBytes;

    /// <summary>Connect with PipeOptions.CurrentUserOnly (recommended). Disable only when attaching to a server running under another account.</summary>
    public bool CurrentUserOnly { get; set; } = true;

    /// <summary>Extra arguments passed to spawned servers (e.g. "--log-level Debug").</summary>
    public string? ServerExtraArgs { get; set; }
}
