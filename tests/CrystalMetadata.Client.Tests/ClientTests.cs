using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Core.Protocol;
using CrystalMetadata.Core.Tests;
using Xunit;

namespace CrystalMetadata.Client.Tests;

public class ClientTests
{
    private static string NewPipe() => "CrystalMetadataTest." + Guid.NewGuid().ToString("N");

    private static ClientOptions Attached(string pipe, int timeoutMs = 5000) => new()
    {
        PipeName = pipe,
        AttachToExistingServer = true,
        ConnectTimeoutMs = 3000,
        RequestTimeoutMs = timeoutMs,
        CurrentUserOnly = true
    };

    private static PipeResponse Ok(ReportMetadata? metadata = null) => new()
    {
        Success = true,
        Metadata = metadata ?? MetadataJsonTests.BuildFullSample(),
        Server = new ServerInfo { ProcessId = 1, Bitness = "x64", ReportsProcessed = 1 }
    };

    [Fact]
    public async Task Extract_Success_ReturnsMetadata()
    {
        var pipe = NewPipe();
        await using var server = new FakePipeServer(pipe, _ => Task.FromResult(FakeBehaviour.Respond(Ok())));
        await using var host = new ExtractorProcessHost(Attached(pipe));
        var client = new CrystalMetadataClient(host, Attached(pipe));

        var result = await client.ExtractAsync(@"C:\x\SalesRegister.rpt");

        Assert.True(result.Success);
        Assert.NotNull(result.Metadata);
        Assert.Equal("SalesRegister", result.Metadata!.ReportInfo.ReportName);
        Assert.Equal(1, result.Attempts);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task Extract_LoadFailed_IsNotRetried()
    {
        var pipe = NewPipe();
        var calls = 0;
        await using var server = new FakePipeServer(pipe, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(FakeBehaviour.Respond(PipeResponse.Failure("", ErrorCodes.LoadFailed, "corrupt")));
        });
        await using var host = new ExtractorProcessHost(Attached(pipe));
        var client = new CrystalMetadataClient(host, Attached(pipe));

        var result = await client.ExtractAsync(@"C:\x\bad.rpt");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.LoadFailed, result.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Extract_ServerDisconnect_IsRetriedOnceThenSucceeds()
    {
        var pipe = NewPipe();
        var calls = 0;
        await using var server = new FakePipeServer(pipe, _ =>
        {
            var n = Interlocked.Increment(ref calls);
            return Task.FromResult(n == 1 ? FakeBehaviour.Disconnect() : FakeBehaviour.Respond(Ok()));
        });
        await using var host = new ExtractorProcessHost(Attached(pipe));
        var client = new CrystalMetadataClient(host, Attached(pipe));

        var result = await client.ExtractAsync(@"C:\x\SalesRegister.rpt");

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Extract_HalfFrame_ReportsServerCrashedAfterRetry()
    {
        var pipe = NewPipe();
        await using var server = new FakePipeServer(pipe, _ => Task.FromResult(FakeBehaviour.HalfFrame()));
        await using var host = new ExtractorProcessHost(Attached(pipe));
        var client = new CrystalMetadataClient(host, Attached(pipe));

        var result = await client.ExtractAsync(@"C:\x\SalesRegister.rpt");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ServerCrashed, result.ErrorCode);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task Extract_Garbage_ReportsServerCrashed()
    {
        var pipe = NewPipe();
        await using var server = new FakePipeServer(pipe, _ => Task.FromResult(FakeBehaviour.Garbage()));
        await using var host = new ExtractorProcessHost(Attached(pipe));
        var client = new CrystalMetadataClient(host, Attached(pipe));

        var result = await client.ExtractAsync(@"C:\x\SalesRegister.rpt");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ServerCrashed, result.ErrorCode);
    }

    [Fact]
    public async Task Extract_NoServer_ReportsServerCrashedQuickly()
    {
        var pipe = NewPipe();
        var options = Attached(pipe);
        options.ConnectTimeoutMs = 500;
        await using var host = new ExtractorProcessHost(options);
        var client = new CrystalMetadataClient(host, options);

        var result = await client.ExtractAsync(@"C:\x\SalesRegister.rpt");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ServerCrashed, result.ErrorCode);
    }

    [Fact]
    public async Task Ping_ReturnsServerInfo()
    {
        var pipe = NewPipe();
        await using var server = new FakePipeServer(pipe, r =>
        {
            Assert.Equal(PipeCommands.Ping, r.Command);
            return Task.FromResult(FakeBehaviour.Respond(new PipeResponse { Success = true, Server = new ServerInfo { ProcessId = 77, PipeName = pipe } }));
        });
        await using var host = new ExtractorProcessHost(Attached(pipe));
        var client = new CrystalMetadataClient(host, Attached(pipe));

        var info = await client.PingAsync();
        Assert.Equal(77, info.ProcessId);
    }

    [Fact]
    public async Task ExtractMany_YieldsAllResultsAndReportsProgress()
    {
        var pipe = NewPipe();
        await using var server = new FakePipeServer(pipe, r => Task.FromResult(
            r.ReportPath!.EndsWith("bad.rpt") ? FakeBehaviour.Respond(PipeResponse.Failure("", ErrorCodes.LoadFailed, "x")) : FakeBehaviour.Respond(Ok())));
        await using var host = new ExtractorProcessHost(Attached(pipe));
        var client = new CrystalMetadataClient(host, Attached(pipe));
        var progress = new List<BatchProgress>();

        var results = new List<ExtractionResult>();
        await foreach (var r in client.ExtractManyAsync(new[] { @"C:\x\a.rpt", @"C:\x\bad.rpt", @"C:\x\c.rpt" }, progress: new SyncProgress(progress)))
        {
            results.Add(r);
        }

        Assert.Equal(3, results.Count);
        Assert.Equal(1, results.Count(r => !r.Success));
        Assert.Equal(3, progress.Count);
        Assert.Equal(3, progress.Last().Completed);
    }

    [Fact]
    public async Task Batch_WritesFilesManifestAndResumes()
    {
        var pipe = NewPipe();
        var calls = 0;
        await using var server = new FakePipeServer(pipe, _ => { Interlocked.Increment(ref calls); return Task.FromResult(FakeBehaviour.Respond(Ok())); });
        await using var host = new ExtractorProcessHost(Attached(pipe));
        var client = new CrystalMetadataClient(host, Attached(pipe));
        var orchestrator = new BatchOrchestrator(client);

        var dir = Path.Combine(Path.GetTempPath(), "cmtest-" + Guid.NewGuid().ToString("N"));
        var inDir = Path.Combine(dir, "in");
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(Path.Combine(inDir, "sub"));
        File.WriteAllText(Path.Combine(inDir, "a.rpt"), "a");
        File.WriteAllText(Path.Combine(inDir, "sub", "b.rpt"), "b");
        try
        {
            var options = new BatchOptions { Inputs = { inDir }, OutputDirectory = outDir, Recursive = true, ComputeHints = true };
            var m1 = await orchestrator.RunAsync(options);
            Assert.Equal(2, m1.Total);
            Assert.Equal(2, m1.Succeeded);
            Assert.True(File.Exists(Path.Combine(outDir, "a.metadata.json")));
            Assert.True(File.Exists(Path.Combine(outDir, "sub", "b.metadata.json")));
            Assert.True(File.Exists(Path.Combine(outDir, BatchOrchestrator.ManifestFileName)));
            var written = MetadataJson.ReadFile<ReportMetadata>(Path.Combine(outDir, "a.metadata.json"))!;
            Assert.NotNull(written.Derived);
            Assert.Equal(2, calls);

            // second run: everything skipped by resume
            var m2 = await orchestrator.RunAsync(options);
            Assert.Equal(2, m2.Skipped);
            Assert.Equal(2, calls);

            // changed input -> re-extracted
            File.WriteAllText(Path.Combine(inDir, "a.rpt"), "a2");
            var m3 = await orchestrator.RunAsync(options);
            Assert.Equal(1, m3.Skipped);
            Assert.Equal(3, calls);

            // --force re-extracts all
            options.Force = true;
            var m4 = await orchestrator.RunAsync(options);
            Assert.Equal(0, m4.Skipped);
            Assert.Equal(5, calls);

            // manifest ordering is deterministic
            var manifest = MetadataJson.ReadFile<BatchManifest>(Path.Combine(outDir, BatchOrchestrator.ManifestFileName))!;
            Assert.Equal(new[] { "a.rpt", Path.Combine("sub", "b.rpt") }, manifest.Entries.Select(e => e.RelativePath).ToArray());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LayoutHints_PairsHeadersWithDetailFields()
    {
        var m = MetadataJsonTests.BuildFullSample();
        LayoutHints.Apply(m);

        Assert.NotNull(m.Derived);
        Assert.Equal(2, m.Derived!.Columns.Count);
        var first = m.Derived.Columns[0];
        Assert.Equal("Field1", first.FieldObjectName);
        Assert.Equal("Invoice No", first.Header);          // explicit FieldHeading link
        Assert.Equal("Heading1", first.HeaderObjectName);
        Assert.Equal("{Get_Sales.InvoiceNo}", first.FieldDataSource);
        var second = m.Derived.Columns[1];
        Assert.Equal("Field2", second.FieldObjectName);
        Assert.Null(second.Header);                        // nothing overlaps x=2000..3500 in the header
        Assert.Equal(2, second.NumberFormat!.DecimalPlaces);
        Assert.Equal(2, m.Derived.Bands.Count);
        Assert.NotNull(m.Subreports[0].Report!.Derived);   // applied recursively
    }

    private sealed class SyncProgress : IProgress<BatchProgress>
    {
        private readonly List<BatchProgress> _list;
        public SyncProgress(List<BatchProgress> list) => _list = list;
        public void Report(BatchProgress value) { lock (_list) _list.Add(value); }
    }
}
