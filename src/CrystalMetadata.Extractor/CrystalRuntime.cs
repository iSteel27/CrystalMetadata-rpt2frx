using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace CrystalMetadata.Extractor
{
    /// <summary>Probes the installed SAP Crystal Reports runtime.</summary>
    public static class CrystalRuntime
    {
        /// <summary>Version of the loaded CrystalDecisions.CrystalReports.Engine assembly, or null when it cannot be loaded.</summary>
        public static string? Version
        {
            get
            {
                try
                {
                    return ReadVersion();
                }
                catch
                {
                    return null;
                }
            }
        }

        public static string Bitness => IntPtr.Size == 8 ? "x64" : "x86";

        /// <summary>
        /// Verifies that the Crystal runtime assemblies can be loaded. Returns false with a human-readable reason otherwise
        /// (typically: runtime MSI not installed, or bitness mismatch).
        /// </summary>
        public static bool Probe(out string message)
        {
            try
            {
                var version = ReadVersion();
                message = "SAP Crystal Reports runtime " + version + " (" + Bitness + ") loaded.";
                return true;
            }
            catch (FileNotFoundException ex)
            {
                message = "Crystal Reports runtime assembly not found: " + ex.Message +
                          " Install 'SAP Crystal Reports runtime engine for .NET Framework' (" + Bitness + ") and make sure the " +
                          "process bitness matches the installed runtime.";
                return false;
            }
            catch (BadImageFormatException ex)
            {
                message = "Crystal Reports runtime bitness mismatch (process is " + Bitness + "): " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                message = "Crystal Reports runtime failed to load: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadVersion()
        {
            var asm = typeof(CrystalDecisions.CrystalReports.Engine.ReportDocument).Assembly;
            // touching the RAS assembly too so a missing 64-bit interop shows up at startup, not on the first report
            var ras = typeof(CrystalDecisions.ReportAppServer.ClientDoc.ISCDReportClientDocument).Assembly;
            return asm.GetName().Version + " / RAS " + ras.GetName().Version;
        }
    }
}
