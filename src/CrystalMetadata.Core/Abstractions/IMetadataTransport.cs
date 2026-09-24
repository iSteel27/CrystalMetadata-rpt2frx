using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Protocol;

namespace CrystalMetadata.Core.Abstractions
{
    /// <summary>Sends one request to an extractor process and returns its response (named pipe in production, in-proc fakes in tests).</summary>
    public interface IMetadataTransport
    {
        Task<PipeResponse> SendAsync(PipeRequest request, CancellationToken ct);
    }
}
