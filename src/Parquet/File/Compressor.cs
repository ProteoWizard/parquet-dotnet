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
        // It was never reverted, and the evidence against it is stronger than just that
        // commit message: the investigating session ALSO ran a standalone test compressing
        // 40 realistic buffers sequentially vs under Parallel.For, with a shared Iron AND
        // with per-thread instances - 0 mismatches either way. zstd is deterministic under
        // concurrency. See "What was ruled out along the way" in
        // TODO-20260909_osprey_parallel_parquet_write.md.
        //
        // So this is an UNPROVEN change kept on a disproven premise. It is NOT a
        // determinism fix, must not be described as one, and is NOT an upstream
        // parquet-dotnet bug to report. Reverting it is the better-supported option; the
        // only cost is that the shipped binary would no longer be the one benchmarked.
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
