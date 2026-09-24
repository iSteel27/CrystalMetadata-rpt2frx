using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CrystalMetadata.ConsoleApp;

/// <summary>
/// Guided terminal menu shown when the console starts without arguments (F5 in Visual Studio, or double-click).
/// Every option asks for what it needs, shows the equivalent command line, and runs it through the normal pipeline.
/// </summary>
internal static class InteractiveMenu
{
    public static async Task<int> RunAsync()
    {
        while (true)
        {
            PrintMenu();
            Console.Write("Choose an option [1-7, 0 to exit]: ");
            var choice = (Console.ReadLine() ?? "0").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1":
                    await ExtractOneAsync();
                    break;
                case "2":
                    await ExtractFolderAsync();
                    break;
                case "3":
                    await RunAndReportAsync(new[] { "doctor" });
                    break;
                case "4":
                    await SchemaAsync();
                    break;
                case "5":
                    PrintHelp();
                    break;
                case "6":
                    await AdvancedAsync();
                    break;
                case "7":
                    await ConvertAsync();
                    break;
                case "0":
                case "q":
                case "exit":
                    return 0;
                default:
                    Console.WriteLine("Please type a number from the menu.");
                    break;
            }

            Console.WriteLine();
            Console.Write("Press Enter to return to the menu...");
            Console.ReadLine();
            try { Console.Clear(); } catch (IOException) { /* output is redirected */ }
        }
    }

    private static void PrintMenu()
    {
        Console.WriteLine("==============================================================");
        Console.WriteLine("  CrystalMetadata  -  Crystal Reports (.rpt) metadata extractor");
        Console.WriteLine("==============================================================");
        Console.WriteLine("  Reads report DESIGN only (layout, fields, formulas, groups,");
        Console.WriteLine("  parameters, subreports). It never reads report data.");
        Console.WriteLine();
        Console.WriteLine("  1  Extract ONE report            -> <name>.metadata.json (+ FastReport .frx if you say yes)");
        Console.WriteLine("  2  Extract a FOLDER of reports   -> one JSON per report + manifest (+ .frx files)");
        Console.WriteLine("  3  Check my setup                -> is the Crystal runtime OK?");
        Console.WriteLine("  4  Create the JSON Schema file   -> describes the output format");
        Console.WriteLine("  5  Help / all command-line options");
        Console.WriteLine("  6  Advanced: type any command yourself");
        Console.WriteLine("  7  Convert a report to a FastReport template (.frx)  [open source]");
        Console.WriteLine("  0  Exit");
        Console.WriteLine();
    }

    private static async Task ExtractOneAsync()
    {
        Console.WriteLine("EXTRACT ONE REPORT");
        Console.WriteLine("Tip: you can drag the .rpt file into this window to paste its path.");
        var file = AskPath("Path of the .rpt file", mustBeFile: true);
        if (file == null) return;

        var defaultOut = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".metadata.json");
        Console.WriteLine();
        Console.WriteLine("The JSON will be saved here:");
        Console.WriteLine("  " + defaultOut);
        var output = defaultOut;
        if (!AskYesNo("Save it there?", true))
        {
            output = AskOutputFile("Type the full path of the .json file to create", defaultOut);
        }

        var hints = AskYesNo("Add layout hints (columns paired with their headers)?", true);
        var alsoFrx = AskYesNo("Also create a FastReport template (.frx) from it?", true);

        var args = new List<string> { "extract", "--in", file, "--out", output };
        if (hints) args.Add("--hints");
        var code = await RunAndReportAsync(args.ToArray());

        if (alsoFrx && code == 0 && File.Exists(output))
        {
            Console.WriteLine();
            Console.WriteLine("FASTREPORT TEMPLATE");
            await ConvertJsonAsync(output);
        }
    }

    private static async Task ExtractFolderAsync()
    {
        Console.WriteLine("EXTRACT A FOLDER OF REPORTS");
        Console.WriteLine("Tip: you can drag the folder into this window to paste its path.");
        var folder = AskPath("Folder that contains .rpt files", mustBeFile: false);
        if (folder == null) return;

        var recursive = AskYesNo("Include sub-folders?", true);
        var defaultOut = Path.Combine(folder, "metadata");
        Console.WriteLine();
        Console.WriteLine("One JSON per report will be saved in this folder:");
        Console.WriteLine("  " + defaultOut);
        var output = defaultOut;
        if (!AskYesNo("Save them there?", true))
        {
            output = AskOutputFolder("Type the full path of the output folder", defaultOut);
        }

        var workersText = Ask("Parallel extractor processes (1-4; more = faster, more memory)", "2");
        var hints = AskYesNo("Add layout hints?", true);
        var force = AskYesNo("Re-extract reports that were already done? (No = skip unchanged ones)", false);
        var alsoFrx = AskYesNo("Also create a FastReport template (.frx) for every report?", true);
        var frxOptions = alsoFrx ? AskFastReportOptions(askPreview: true) : new List<string>();

        var args = new List<string> { "batch", "--in", folder, "--out", output, "--workers", int.TryParse(workersText, out var w) && w > 0 ? w.ToString() : "2" };
        if (recursive) args.Add("--recursive");
        if (hints) args.Add("--hints");
        if (force) args.Add("--force");
        var code = await RunAndReportAsync(args.ToArray());

        if (alsoFrx && Directory.Exists(output))
        {
            Console.WriteLine();
            Console.WriteLine("FASTREPORT TEMPLATES");
            var jsonFiles = Directory.GetFiles(output, "*.metadata.json", SearchOption.AllDirectories).OrderBy(f => f).ToList();
            foreach (var json in jsonFiles)
            {
                var name = Path.GetFileName(json);
                name = name[..^".metadata.json".Length];
                var frx = Path.Combine(Path.GetDirectoryName(json)!, name + ".frx");
                var convertArgs = new List<string> { "convert", "--in", json, "--out", frx };
                convertArgs.AddRange(frxOptions);
                await RunAndReportAsync(convertArgs.ToArray());
            }

            Console.WriteLine();
            Console.WriteLine($"{jsonFiles.Count} template(s) written next to the JSON files in {output}");
        }
    }

    private static async Task ConvertAsync()
    {
        Console.WriteLine("CONVERT TO A FASTREPORT (.frx) TEMPLATE");
        Console.WriteLine("Give either a Crystal .rpt file or a .metadata.json file made by option 1.");
        Console.WriteLine("Tip: you can drag the file into this window to paste its path.");
        var file = AskPath("Path of the .rpt or .metadata.json file", mustBeFile: true);
        if (file == null) return;

        var name = Path.GetFileName(file);
        foreach (var suffix in new[] { ".metadata.json", ".rpt", ".json" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        }

        var defaultOut = Path.Combine(Path.GetDirectoryName(file)!, name + ".frx");
        Console.WriteLine();
        Console.WriteLine("The template will be saved here:");
        Console.WriteLine("  " + defaultOut);
        var output = defaultOut;
        if (!AskYesNo("Save it there?", true))
        {
            output = AskOutputFile("Type the full path of the .frx file to create", defaultOut);
        }

        var args = new List<string> { "convert", "--in", file, "--out", output };
        args.AddRange(AskFastReportOptions(askPreview: true));
        await RunAndReportAsync(args.ToArray());
    }

    private static async Task SchemaAsync()
    {
        Console.WriteLine("CREATE JSON SCHEMA");
        var output = Ask("Save schema as", Path.Combine(Environment.CurrentDirectory, "metadata.schema.json"));
        await RunAndReportAsync(new[] { "schema", "--out", output });
    }

    private static async Task AdvancedAsync()
    {
        Console.WriteLine("ADVANCED - type a full command (without the program name), for example:");
        Console.WriteLine("  extract --in \"C:\\Reports\\Sales.rpt\" --out Sales.json --hints");
        Console.WriteLine("  batch --in \"C:\\Reports\" --recursive --out out --workers 4");
        Console.Write("> ");
        var line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) return;
        await RunAndReportAsync(SplitCommandLine(line));
    }

    private static void PrintHelp()
    {
        Program.PrintUsage();
        Console.WriteLine();
        Console.WriteLine("Output: one .metadata.json per report. See docs\\metadata-schema.md for every property.");
        Console.WriteLine("Positions are in twips (1440 per inch); every value also has a *Points version.");
        Console.WriteLine("null in the JSON means 'Crystal does not expose this'. 'diagnostics' lists anything that could not be read.");
    }

    private static async Task<int> RunAndReportAsync(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("Running:  rpt2frx " + string.Join(" ", args.Select(Quote)));
        Console.WriteLine("--------------------------------------------------------------");

        var code = await Program.RunAsync(args);

        Console.WriteLine("--------------------------------------------------------------");
        switch (code)
        {
            case 0:
                Console.WriteLine("Finished successfully.");
                break;
            case 3:
                Console.WriteLine("Could not start the extractor (exit code 3). Usual causes:");
                Console.WriteLine("  - the solution is not built: in Visual Studio use Build > Build Solution (same Debug/Release as this app)");
                Console.WriteLine("  - the 64-bit SAP Crystal Reports runtime is not installed");
                break;
            case 2:
                Console.WriteLine("Command not understood (exit code 2). Choose 5 to see the options.");
                break;
            default:
                Console.WriteLine("Finished with problems (exit code " + code + "). Read the messages above.");
                break;
        }

        return code;
    }

    /// <summary>Asks the FastReport options (preview, font scale, logo) and converts one metadata JSON to a .frx next to it.</summary>
    private static async Task ConvertJsonAsync(string jsonPath, bool askPreview = true)
    {
        var name = Path.GetFileName(jsonPath);
        foreach (var suffix in new[] { ".metadata.json", ".json" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        }

        var frx = Path.Combine(Path.GetDirectoryName(jsonPath)!, name + ".frx");
        var args = new List<string> { "convert", "--in", jsonPath, "--out", frx };
        var options = AskFastReportOptions(askPreview);
        args.AddRange(options);
        await RunAndReportAsync(args.ToArray());
    }

    private static List<string> AskFastReportOptions(bool askPreview)
    {
        var args = new List<string>();
        if (askPreview && AskYesNo("Also render a preview (PDF + PNG) with SAMPLE data so you can check the look?", true))
        {
            args.Add("--preview");
        }

        if (AskYesNo("Match Crystal's text size (font scale 0.87)? Crystal's PDF draws text narrower than FastReport.", true))
        {
            args.Add("--font-scale");
            args.Add("0.87");
        }

        var logo = Ask("Logo image to place in picture boxes (.png/.jpg, empty = none)", "");
        if (logo.Length > 0 && File.Exists(logo))
        {
            args.Add("--picture");
            args.Add(Path.GetFullPath(logo));
        }
        else if (logo.Length > 0)
        {
            Console.WriteLine("  File not found, continuing without a logo.");
        }

        return args;
    }

    // ------------------------------------------------------------------ prompts

    private static string Ask(string question, string? defaultValue)
    {
        Console.Write(defaultValue == null ? $"{question}: " : $"{question} [{defaultValue}]: ");
        var text = (Console.ReadLine() ?? string.Empty).Trim();
        text = Unquote(text);
        return text.Length == 0 && defaultValue != null ? defaultValue : text;
    }

    private static bool AskYesNo(string question, bool defaultYes)
    {
        Console.Write($"{question} [{(defaultYes ? "Y/n" : "y/N")}]: ");
        var text = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return defaultYes;
        return text is "y" or "yes";
    }

    /// <summary>Asks for an output .json path; rejects yes/no answers and relative names, appends .json when missing.</summary>
    /// <summary>
    /// Asks for an output file path. The required extension comes from <paramref name="defaultValue"/> (.json or .frx).
    /// A folder is accepted: the default file name is placed inside it. Relative names go next to the default location.
    /// </summary>
    private static string AskOutputFile(string question, string defaultValue)
    {
        var extension = Path.GetExtension(defaultValue);
        while (true)
        {
            var text = Ask(question, defaultValue);
            if (text.Length <= 2 && text.ToLowerInvariant() is "y" or "n" or "yes" or "no")
            {
                Console.WriteLine("  That looks like a yes/no answer. Please type a file path, or press Enter for the default.");
                continue;
            }

            if (!Path.IsPathRooted(text))
            {
                text = Path.Combine(Path.GetDirectoryName(defaultValue)!, text);
            }

            // a folder was typed: keep the default file name and put it inside that folder
            if (Directory.Exists(text) || text.EndsWith("\\", StringComparison.Ordinal) || text.EndsWith("/", StringComparison.Ordinal))
            {
                text = Path.Combine(text, Path.GetFileName(defaultValue));
            }

            if (!string.IsNullOrEmpty(extension) && !string.Equals(Path.GetExtension(text), extension, StringComparison.OrdinalIgnoreCase))
            {
                text += extension;
            }

            return Path.GetFullPath(text);
        }
    }

    private static string AskOutputFolder(string question, string defaultValue)
    {
        while (true)
        {
            var text = Ask(question, defaultValue);
            if (text.Length <= 2 && text.ToLowerInvariant() is "y" or "n" or "yes" or "no")
            {
                Console.WriteLine("  That looks like a yes/no answer. Please type a folder path, or press Enter for the default.");
                continue;
            }

            return Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(Path.GetDirectoryName(defaultValue)!, text));
        }
    }

    private static string? AskPath(string question, bool mustBeFile)
    {
        while (true)
        {
            var text = Ask(question + " (empty = cancel)", null);
            if (text.Length == 0) return null;

            var full = Path.GetFullPath(text);
            if (mustBeFile ? File.Exists(full) : Directory.Exists(full)) return full;

            Console.WriteLine(mustBeFile ? $"  File not found: {full}" : $"  Folder not found: {full}");
        }
    }

    private static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    private static string Quote(string s) => s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s;

    /// <summary>Splits a command line honouring double quotes.</summary>
    private static string[] SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0) parts.Add(current.ToString());
        return parts.ToArray();
    }
}
