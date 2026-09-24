using System;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace CrystalMetadata.Extractor
{
    /// <summary>
    /// Per-report (main or subreport) extraction state: options, logger and the diagnostic sink. Every SDK access in the
    /// walkers goes through <see cref="Try"/> so that a failure becomes a diagnostic and extraction continues.
    /// </summary>
    internal sealed class ExtractionContext
    {
        public ExtractionContext(string scope, ExtractionOptions options, ILogger logger)
        {
            Scope = scope;
            Options = options;
            Logger = logger;
            Diagnostics = new DiagnosticSink(scope);
        }

        public string Scope { get; }
        public ExtractionOptions Options { get; }
        public ILogger Logger { get; }
        public DiagnosticSink Diagnostics { get; }

        public ExtractionContext ForSubreport(string subreportName)
        {
            return new ExtractionContext(subreportName, Options, Logger);
        }

        public void Info(string code, string path, string message) => Diagnostics.Add(DiagnosticLevel.Info, code, path, message);

        public void Warn(string code, string path, string message)
        {
            Diagnostics.Add(DiagnosticLevel.Warning, code, path, message);
            Logger.LogWarning("[{Scope}] {Code} at {Path}: {Message}", Scope, code, path, message);
        }

        public void Error(string code, string path, string message)
        {
            Diagnostics.Add(DiagnosticLevel.Error, code, path, message);
            Logger.LogError("[{Scope}] {Code} at {Path}: {Message}", Scope, code, path, message);
        }

        /// <summary>Runs <paramref name="action"/>; on exception records a diagnostic and returns false.</summary>
        public bool Try(string path, string code, Action action, DiagnosticLevel level = DiagnosticLevel.Warning)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                Record(path, code, ex, level);
                return false;
            }
        }

        /// <summary>Runs <paramref name="func"/>; on exception records a diagnostic and returns null.</summary>
        public T? Try<T>(string path, string code, Func<T> func, DiagnosticLevel level = DiagnosticLevel.Warning) where T : class
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                Record(path, code, ex, level);
                return null;
            }
        }

        /// <summary>Runs <paramref name="func"/>; on exception records a diagnostic and returns <paramref name="fallback"/>.</summary>
        public T TryValue<T>(string path, string code, Func<T> func, T fallback, DiagnosticLevel level = DiagnosticLevel.Info)
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                Record(path, code, ex, level);
                return fallback;
            }
        }

        /// <summary>Like <see cref="TryValue{T}"/> but silent (no diagnostic) — for optional properties that are known to throw on some SDK builds.</summary>
        public T? Quiet<T>(Func<T> func) where T : struct
        {
            try
            {
                return func();
            }
            catch
            {
                return null;
            }
        }

        public T? QuietRef<T>(Func<T?> func) where T : class
        {
            try
            {
                return func();
            }
            catch
            {
                return null;
            }
        }

        private void Record(string path, string code, Exception ex, DiagnosticLevel level)
        {
            var diag = Diagnostic.FromException(level, code, Scope, path, ex);
            Diagnostics.Add(diag);
            // exception objects (stack traces) only at Error level; warnings carry the message, the JSON carries the type
            if (level == DiagnosticLevel.Error)
            {
                Logger.LogError(ex, "[{Scope}] {Code} at {Path}: {Message}", Scope, code, path, ex.Message);
            }
            else
            {
                Logger.Log(level == DiagnosticLevel.Warning ? LogLevel.Warning : LogLevel.Debug, "[{Scope}] {Code} at {Path}: {ExceptionType}: {Message}", Scope, code, path, ex.GetType().Name, ex.Message);
            }
        }
    }
}
