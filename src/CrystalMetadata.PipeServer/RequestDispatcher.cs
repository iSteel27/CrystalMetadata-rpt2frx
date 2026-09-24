using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Abstractions;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Protocol;
using CrystalMetadata.Extractor;
using Microsoft.Extensions.Logging;

namespace CrystalMetadata.PipeServer
{
    /// <summary>Turns one <see cref="PipeRequest"/> into one <see cref="PipeResponse"/>. Never throws.</summary>
    public sealed class RequestDispatcher
    {
        private readonly StaExtractionWorker _worker;
        private readonly ServerState _state;
        private readonly ExtractionDefaults _defaults;
        private readonly ILogger<RequestDispatcher> _logger;

        public RequestDispatcher(StaExtractionWorker worker, ServerState state, ExtractionDefaults defaults, ILogger<RequestDispatcher> logger)
        {
            _worker = worker;
            _state = state;
            _defaults = defaults;
            _logger = logger;
        }

        /// <summary>Set when a "shutdown" command was received.</summary>
        public bool ShutdownRequested { get; private set; }

        public async Task<PipeResponse> HandleAsync(PipeRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var requestId = string.IsNullOrEmpty(request.RequestId) ? Guid.NewGuid().ToString("D") : request.RequestId;
            PipeResponse response;

            using (_logger.BeginScope("RequestId={RequestId} ReportPath={ReportPath}", requestId, request.ReportPath ?? string.Empty))
            {
                try
                {
                    if (request.ProtocolVersion > ProtocolConstants.Version)
                    {
                        response = PipeResponse.Failure(requestId, ErrorCodes.ProtocolError, "Unsupported protocol version " + request.ProtocolVersion + " (server supports " + ProtocolConstants.Version + ").");
                    }
                    else
                    {
                        switch ((request.Command ?? PipeCommands.Extract).Trim().ToLowerInvariant())
                        {
                            case PipeCommands.Ping:
                            case PipeCommands.Info:
                                response = new PipeResponse { RequestId = requestId, Success = true };
                                break;
                            case PipeCommands.Shutdown:
                                ShutdownRequested = true;
                                response = new PipeResponse { RequestId = requestId, Success = true };
                                break;
                            case PipeCommands.Extract:
                                response = await ExtractAsync(requestId, request, ct).ConfigureAwait(false);
                                break;
                            default:
                                response = PipeResponse.Failure(requestId, ErrorCodes.UnknownCommand, "Unknown command '" + request.Command + "'.");
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error while dispatching request");
                    response = PipeResponse.Failure(requestId, ErrorCodes.Internal, ex.Message, ex.GetType().FullName);
                }
            }

            response.Server = _state.ToInfo();
            response.DurationMs = sw.ElapsedMilliseconds;
            return response;
        }

        private async Task<PipeResponse> ExtractAsync(string requestId, PipeRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.ReportPath))
            {
                return PipeResponse.Failure(requestId, ErrorCodes.ProtocolError, "reportPath is required for the extract command.");
            }

            var options = request.Options ?? new ExtractionOptions
            {
                IncludeUnusedFields = _defaults.IncludeUnusedFields,
                IncludeRawBags = _defaults.IncludeRawBags,
                IncludeGeneratedSql = _defaults.IncludeGeneratedSql
            };
            if (options.TimeoutMs <= 0)
            {
                options.TimeoutMs = _state.Options.RequestTimeoutMs;
            }

            if (!string.IsNullOrEmpty(options.OutputPath) && !_state.Options.AllowOutputPath)
            {
                return PipeResponse.Failure(requestId, ErrorCodes.OutputPathNotAllowed, "The server is not configured to write output files (AllowOutputPath=false).");
            }

            try
            {
                var metadata = await _worker.ExtractAsync(request.ReportPath!, options, ct).ConfigureAwait(false);
                _state.IncrementProcessed();

                var response = new PipeResponse { RequestId = requestId, Success = true, Diagnostics = metadata.Diagnostics };
                if (!string.IsNullOrEmpty(options.OutputPath))
                {
                    MetadataJson.WriteFile(options.OutputPath!, metadata);
                    response.OutputPath = Path.GetFullPath(options.OutputPath!);
                }
                else
                {
                    response.Metadata = metadata;
                }

                return response;
            }
            catch (ReportLoadException ex)
            {
                _state.IncrementProcessed();
                _logger.LogWarning(ex, "Load failed ({Code}) for {Path}", ex.Code, request.ReportPath);
                if (ex.Code == ErrorCodes.PrintJobLimit)
                {
                    _state.RequestRecycle("print job limit");
                }

                return PipeResponse.Failure(requestId, ex.Code, ex.Message, ex.InnerException?.Message);
            }
            catch (TimeoutException ex)
            {
                _state.RequestRecycle("request timeout");
                return PipeResponse.Failure(requestId, ErrorCodes.Timeout, ex.Message);
            }
            catch (OperationCanceledException)
            {
                return PipeResponse.Failure(requestId, ErrorCodes.Internal, "Request cancelled.");
            }
            catch (Exception ex)
            {
                _state.IncrementProcessed();
                _logger.LogError(ex, "Extraction failed for {Path}", request.ReportPath);
                return PipeResponse.Failure(requestId, ErrorCodes.Internal, ex.Message, ex.GetType().FullName);
            }
        }
    }
}
