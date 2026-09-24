using System;
using System.IO;
using CrystalMetadata.Core.Abstractions;
using CrystalMetadata.Core.Protocol;

namespace CrystalMetadata.Extractor
{
    /// <summary>Maps exceptions thrown by <c>ReportDocument.Load</c> to stable error codes.</summary>
    internal static class LoadFailureClassifier
    {
        public static ReportLoadException Classify(string path, Exception ex)
        {
            var msg = Flatten(ex);
            var lower = msg.ToLowerInvariant();

            if (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return new ReportLoadException(ErrorCodes.FileNotFound, "Report file not found: " + path, ex);
            }

            if (ex is UnauthorizedAccessException || lower.Contains("access is denied") || lower.Contains("access denied"))
            {
                return new ReportLoadException(ErrorCodes.AccessDenied, "Access denied: " + path, ex);
            }

            if (lower.Contains("jrc engine") || lower.Contains("c++ stack"))
            {
                return new ReportLoadException(ErrorCodes.JrcReport, "Report was saved by the Java Reporting Component and cannot be opened by the .NET runtime: " + path, ex);
            }

            if (lower.Contains("print job limit") || lower.Contains("processing jobs limit") || lower.Contains("maximum report processing"))
            {
                return new ReportLoadException(ErrorCodes.PrintJobLimit, "Crystal print-job limit reached; the extractor process must be recycled.", ex);
            }

            if (lower.Contains("failed to load database information") || lower.Contains("database dll") || lower.Contains("crdb_"))
            {
                return new ReportLoadException(ErrorCodes.DbDriverMissing, "Database driver required by the report is not installed: " + msg, ex);
            }

            if (lower.Contains("file could not be found") || lower.Contains("could not be opened"))
            {
                return new ReportLoadException(ErrorCodes.FileNotFound, msg, ex);
            }

            return new ReportLoadException(ErrorCodes.LoadFailed, "Load failed for " + path + ": " + msg, ex);
        }

        public static string Flatten(Exception ex)
        {
            var parts = new System.Text.StringBuilder();
            Exception? cur = ex;
            var depth = 0;
            while (cur != null && depth++ < 6)
            {
                if (parts.Length > 0)
                {
                    parts.Append(" -> ");
                }

                parts.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                cur = cur.InnerException;
            }

            return parts.ToString();
        }
    }
}
