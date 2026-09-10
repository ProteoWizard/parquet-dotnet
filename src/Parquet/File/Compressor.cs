using System;
using System.IO.Compression;
using System.Threading;
using IronCompress;

namespace Parquet.File {
    static class Compressor {
        // Patch (osprey): THREAD-LOCAL, not a single shared instance.
        //
        // HISTORY - read before trusting this. This was introduced while chasing
        // non-reproducible bytes under concurrent column compression, on the theory that a
        // shared Iron carried codec state across threads. That theory is WRONG and the
        // commit that added this (915984a) says so itself: "Making Iron thread-local did
        // NOT fix it." The real cause was our own bool encoder writing an uncleared
        // ArrayPool rental, fixed one commit later in 932ade5, which is what actually made
        // the output reproducible (sequential and two parallel runs all sha 87E505583C14).
        //
        // It was never reverted. It is retained ONLY as a cheap thread-safety precaution
        // now that Compress runs concurrently - IronCompress makes no documented
        // thread-safety guarantee - and it costs one allocation per worker thread. It is
        // NOT a determinism fix and must not be described as one, and it is NOT an
        // upstream parquet-dotnet bug to report.
        //
        // Note this also routes Decompress through the ThreadLocal, so it touches the READ
        // path of a binary Skyline ships. Reverting it is a live option; see PATCH-NOTES.
        private static readonly ThreadLocal<Iron> _iron = new ThreadLocal<Iron>(() => new Iron());

        public static IronCompressResult Compress(CompressionMethod method, ReadOnlySpan<byte> input, CompressionLevel compressionLevel) => _iron.Value!.Compress(ToCodec(method), input, compressionLevel: compressionLevel);

        public static IronCompressResult Decompress(CompressionMethod method, ReadOnlySpan<byte> input, int outLength) => _iron.Value!.Decompress(ToCodec(method), input, outLength);

        private static Codec ToCodec(CompressionMethod method) {
            switch(method) {
                case CompressionMethod.Snappy:
                    return Codec.Snappy;
                case CompressionMethod.Gzip:
                    return Codec.Gzip;
                case CompressionMethod.Lzo:
                    return Codec.LZO;
                case CompressionMethod.Brotli:
                    return Codec.Brotli;
                case CompressionMethod.LZ4:
                    return Codec.LZ4;
                case CompressionMethod.Zstd:
                    return Codec.Zstd;
                default:
                    throw new NotSupportedException($"{method} not supported");
            }
        }
    }
}
