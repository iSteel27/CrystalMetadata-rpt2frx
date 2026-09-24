using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using CrystalMetadata.Core.Protocol;
using Microsoft.Win32;

namespace CrystalMetadata.ConsoleApp;

/// <summary>
/// "Check my setup": verifies every requirement and, for each problem, says what it means for the user and exactly how to
/// fix it. The installer runs the same checks (in its own language) before installing.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Doctor
{
    internal const string SapRuntimeUrl = "https://pages.community.sap.com/topics/crystal-reports/visual-studio";
    internal const string NetFramework48Url = "https://dotnet.microsoft.com/download/dotnet-framework/net48";

    private enum Status { Ok, Warn, Fail }

    private sealed record Check(string Name, Status Status, string Detail, string[] Fix);

    public static async Task<int> RunAsync(Func<Task<ServerInfo>> pingExtractor)
    {
        Console.WriteLine("CrystalMetadata - setup check");
        Console.WriteLine("=============================");
        var results = new List<Check>();

        results.Add(CheckWindows());
        results.Add(CheckFastReport());
        var framework = CheckNetFramework();
        results.Add(framework);
        var crystal = CheckCrystalRuntime();
        results.Add(crystal);
        results.Add(await CheckExtractorAsync(pingExtractor, crystal.Status == Status.Ok, framework.Status == Status.Ok));

        foreach (var c in results)
        {
            var tag = c.Status switch { Status.Ok => "[ OK ]", Status.Warn => "[WARN]", _ => "[FAIL]" };
            var colour = c.Status switch { Status.Ok => ConsoleColor.Green, Status.Warn => ConsoleColor.Yellow, _ => ConsoleColor.Red };
            Write(tag, colour);
            Console.WriteLine(" " + c.Name + (c.Detail.Length > 0 ? " - " + c.Detail : string.Empty));
            foreach (var line in c.Fix)
            {
                Console.WriteLine("        " + line);
            }
        }

        var failed = results.Count(c => c.Status == Status.Fail);
        var warned = results.Count(c => c.Status == Status.Warn);
        Console.WriteLine();
        if (failed == 0 && warned == 0)
        {
            Console.WriteLine("Everything is ready. You can extract .rpt files and convert them to FastReport templates.");
        }
        else
        {
            Console.WriteLine($"{failed} problem(s), {warned} warning(s). Fix the items marked [FAIL] above, then run this check again (menu option 3).");
            if (crystal.Status != Status.Ok)
            {
                Console.WriteLine("Note: converting an existing .metadata.json file to a FastReport template does NOT need the Crystal runtime.");
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private static void Write(string text, ConsoleColor colour)
    {
        var previous = Console.ForegroundColor;
        try { Console.ForegroundColor = colour; } catch (IOException) { /* redirected */ }
        Console.Write(text);
        try { Console.ForegroundColor = previous; } catch (IOException) { /* redirected */ }
    }

    // ------------------------------------------------------------------------------------------------ individual checks

    private static Check CheckWindows()
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return new Check("Windows 64-bit", Status.Fail, "this is a 32-bit Windows",
                new[] { "CrystalMetadata needs 64-bit Windows 10 or 11. There is no 32-bit version." });
        }

        return new Check("Windows 64-bit", Status.Ok, Environment.OSVersion.VersionString, Array.Empty<string>());
    }

    private static Check CheckFastReport()
    {
        var error = CrystalMetadata.FastReport.FrxPreview.SelfTest();
        return error == null
            ? new Check("FastReport engine", Status.Ok, "built in, test page rendered", Array.Empty<string>())
            : new Check("FastReport engine", Status.Fail, error, new[]
            {
                "The built-in FastReport engine could not render a test page, so templates cannot be previewed.",
                "Fix: reinstall CrystalMetadata (a file may be missing or blocked). If it keeps happening, send this message to support."
            });
    }

    private static Check CheckNetFramework()
    {
        const int Net48 = 528040;
        int? release = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            release = key?.GetValue("Release") as int?;
        }
        catch (Exception)
        {
            // treated as not installed below
        }

        if (release >= Net48)
        {
            return new Check(".NET Framework 4.8", Status.Ok, "installed", Array.Empty<string>());
        }

        return new Check(".NET Framework 4.8", Status.Fail, release == null ? "not installed" : "an older version is installed", new[]
        {
            "Why it matters: the part that reads .rpt files runs on .NET Framework 4.8.",
            "Fix: install .NET Framework 4.8 (free, from Microsoft): " + NetFramework48Url,
            "      Windows 10 (May 2019 update or later) and Windows 11 already include it - run Windows Update if it is missing."
        });
    }

    /// <summary>The SAP runtime installs its assemblies into the GAC; the 64-bit interop assembly tells the bitness.</summary>
    private static Check CheckCrystalRuntime()
    {
        // CRYSTALMETA_WINDIR lets support and tests point the check at another folder (e.g. to reproduce a PC without the runtime)
        var windows = Environment.GetEnvironmentVariable("CRYSTALMETA_WINDIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var roots = new[] { Path.Combine(windows, "assembly"), Path.Combine(windows, "Microsoft.NET", "assembly") };

        string? Find(string gac, string assembly) =>
            roots.Select(r => Path.Combine(r, gac, assembly)).FirstOrDefault(Directory.Exists);

        var engine = Find("GAC_MSIL", "CrystalDecisions.CrystalReports.Engine");
        var ras64 = Find("GAC_64", "CrystalDecisions.ReportAppServer.ClientDoc");
        var ras32 = Find("GAC_32", "CrystalDecisions.ReportAppServer.ClientDoc");

        if (engine != null && ras64 != null)
        {
            var version = Directory.GetDirectories(ras64).Select(Path.GetFileName).FirstOrDefault() ?? "?";
            return new Check("SAP Crystal Reports runtime (64-bit)", Status.Ok, "installed (" + version.Split("__")[0] + ")", Array.Empty<string>());
        }

        var howTo = new[]
        {
            "How to fix:",
            "  1. Open the SAP page: " + SapRuntimeUrl,
            "  2. Download \"SAP Crystal Reports runtime engine for .NET Framework\" - the 64-bit package (CRRuntime_64bit_13_0_xx.msi).",
            "  3. Run it, then run this check again (menu option 3).",
            "You only need this to READ .rpt files. Converting an existing .metadata.json to a FastReport template works without it."
        };

        if (ras32 != null && ras64 == null)
        {
            return new Check("SAP Crystal Reports runtime (64-bit)", Status.Fail, "only the 32-bit runtime is installed",
                new[] { "Why it matters: CrystalMetadata is 64-bit and cannot use the 32-bit runtime. Install the 64-bit one next to it (they can coexist)." }.Concat(howTo).ToArray());
        }

        return new Check("SAP Crystal Reports runtime (64-bit)", Status.Fail, "not installed",
            new[] { "Why it matters: SAP's runtime is what opens .rpt files. It is free but comes from SAP and is not included with CrystalMetadata." }.Concat(howTo).ToArray());
    }

    private static async Task<Check> CheckExtractorAsync(Func<Task<ServerInfo>> ping, bool crystalOk, bool frameworkOk)
    {
        const string Name = "Extractor (reads .rpt files)";
        if (!crystalOk || !frameworkOk)
        {
            return new Check(Name, Status.Warn, "not tested until the items above are fixed", Array.Empty<string>());
        }

        try
        {
            var info = await ping().ConfigureAwait(false);
            return new Check(Name, Status.Ok, $"started (process {info.ProcessId}, {info.Bitness}, Crystal {info.CrystalRuntimeVersion})", Array.Empty<string>());
        }
        catch (Exception ex)
        {
            var message = Flatten(ex);
            var fix = new List<string> { "The extractor program could not start: " + message };
            if (message.Contains("code 3", StringComparison.OrdinalIgnoreCase) || message.Contains("runtime is not installed", StringComparison.OrdinalIgnoreCase))
            {
                fix.Add("Fix: install the 64-bit SAP Crystal Reports runtime (see above).");
            }
            else if (message.Contains("incomplete", StringComparison.OrdinalIgnoreCase) || message.Contains("-532462766", StringComparison.Ordinal))
            {
                fix.Add("Fix: the program files are incomplete or mixed with older ones. Close every CrystalMetadata window, then reinstall CrystalMetadata (or, if you build it yourself: Build > Rebuild Solution).");
            }
            else if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                fix.Add("Fix: the extractor file is missing (an antivirus may have removed it). Reinstall CrystalMetadata and allow it in your antivirus.");
            }
            else
            {
                fix.Add("Fix: close other CrystalMetadata windows and try again. If it still fails, reinstall, and send this message to support.");
            }

            return new Check(Name, Status.Fail, "does not start", fix.ToArray());
        }
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException!)
        {
            parts.Add(e.Message);
            if (e.InnerException == null) break;
        }

        return string.Join(" -> ", parts);
    }
}
