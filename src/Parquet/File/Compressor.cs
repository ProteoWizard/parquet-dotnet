using System;
using System.IO.Compression;
using System.Threading;
using IronCompress;

namespace Parquet.File {
    static class Compressor {
        // Patch (osprey): THREAD-LOCAL, not a single shared instance. Iron holds codec
        // state, and sharing one across threads makes the compressed bytes depend on how
        // concurrent calls interleave: three runs of the same input produced three
        // different files (sizes differing by a few bytes on small, highly-compressible
        // chunks) once column compression ran concurrently. The decompressed values were
        // identical every time, but parquet output has to be reproducible - it is what
        // the regression goldens compare. One Iron per thread restores determinism and
        // costs a single allocation per worker.
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
