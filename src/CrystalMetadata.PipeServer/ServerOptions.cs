using System.Collections.Generic;
using CrystalMetadata.Core.Protocol;

namespace CrystalMetadata.PipeServer
{
    /// <summary>Bound from the "Server" configuration section, command line (--pipe, --max-reports, …) and CRYSTALMETA_* env vars.</summary>
    public sealed class ServerOptions
    {
        public string PipeName { get; set; } = ProtocolConstants.DefaultPipeName;
        public int MaxReportsPerProcess { get; set; } = 200;
        public int RequestTimeoutMs { get; set; } = ProtocolConstants.DefaultTimeoutMs;
        public int MaxFrameBytes { get; set; } = ProtocolConstants.DefaultMaxFrameBytes;
        public bool AllowOutputPath { get; set; }
        public List<string> AllowedSids { get; set; } = new List<string>();
        public string LogDirectory { get; set; } = "logs";

        /// <summary>Exit after serving one request (debugging aid).</summary>
        public bool Once { get; set; }
    }

    /// <summary>Bound from the "Extraction" section: defaults applied when a request carries no options.</summary>
    public sealed class ExtractionDefaults
    {
        public bool IncludeUnusedFields { get; set; } = true;
        public bool IncludeRawBags { get; set; } = true;
        public bool IncludeGeneratedSql { get; set; }
    }
}
