using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CrystalMetadata.Core.Protocol
{
    /// <summary>Thrown when a frame announces a payload larger than the configured maximum.</summary>
    public sealed class FrameTooLargeException : IOException
    {
        public FrameTooLargeException(int announced, int max)
            : base("Frame of " + announced + " bytes exceeds the maximum of " + max + " bytes.")
        {
            Announced = announced;
            Max = max;
        }

        public int Announced { get; }
        public int Max { get; }
    }

    /// <summary>
    /// Length-prefixed framing over a byte-mode stream: <c>[int32 little-endian length][UTF-8 JSON payload]</c>.
    /// Byte mode + explicit framing is used instead of message-mode pipes so that .NET Framework and .NET 10 behave identically.
    /// </summary>
    public static class FrameCodec
    {
        public const int HeaderSize = 4;

        public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            var header = new byte[HeaderSize];
            header[0] = (byte)(payload.Length & 0xFF);
            header[1] = (byte)((payload.Length >> 8) & 0xFF);
            header[2] = (byte)((payload.Length >> 16) & 0xFF);
            header[3] = (byte)((payload.Length >> 24) & 0xFF);

            await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
            }

            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads one frame. Returns <c>null</c> when the stream ends cleanly at a frame boundary (peer closed).
        /// Throws <see cref="EndOfStreamException"/> when the stream ends in the middle of a frame.
        /// </summary>
        public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxBytes, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            var header = new byte[HeaderSize];
            var got = await ReadUpToAsync(stream, header, HeaderSize, ct).ConfigureAwait(false);
            if (got == 0)
            {
                return null;
            }

            if (got < HeaderSize)
            {
                throw new EndOfStreamException("Stream ended inside a frame header.");
            }

            var length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
            if (length < 0 || length > maxBytes)
            {
                throw new FrameTooLargeException(length, maxBytes);
            }

            var payload = new byte[length];
            if (length == 0)
            {
                return payload;
            }

            var read = await ReadUpToAsync(stream, payload, length, ct).ConfigureAwait(false);
            if (read < length)
            {
                throw new EndOfStreamException("Stream ended inside a frame payload (" + read + "/" + length + " bytes).");
            }

            return payload;
        }

        private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            var total = 0;
            while (total < count)
            {
                var n = await stream.ReadAsync(buffer, total, count - total, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }
    }
}
