using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using IronCompress;
using Microsoft.IO;
using Parquet.Data;
using Parquet.Encodings;
using Parquet.Extensions;
using Parquet.Meta;
using Parquet.Schema;

namespace Parquet.File {
    class DataColumnWriter {
        private readonly Stream _stream;
        private readonly ThriftFooter _footer;
        private readonly SchemaElement _schemaElement;
        private readonly CompressionMethod _compressionMethod;
        private readonly CompressionLevel _compressionLevel;
        private readonly Dictionary<string, string>? _keyValueMetadata;
        private readonly ParquetOptions _options;
        private static readonly RecyclableMemoryStreamManager _rmsMgr = new RecyclableMemoryStreamManager();

        public DataColumnWriter(
           Stream stream,
           ThriftFooter footer,
           SchemaElement schemaElement,
           CompressionMethod compressionMethod,
           ParquetOptions options,
           CompressionLevel compressionLevel,
           Dictionary<string, string>? keyValueMetadata) {
            _stream = stream;
            _footer = footer;
            _schemaElement = schemaElement;
            _compressionMethod = compressionMethod;
            _compressionLevel = compressionLevel;
            _keyValueMetadata = keyValueMetadata;
            _options = options;
        }

        public async Task<ColumnChunk> WriteAsync(
            FieldPath fullPath, DataColumn column,
            CancellationToken cancellationToken = default) {

            using PreparedColumn prepared = await PrepareAsync(fullPath, column, cancellationToken);
            return await EmitAsync(prepared, cancellationToken);
        }

        /// <summary>
        /// A column chunk fully encoded and compressed into memory, ready to be appended
        /// to the output stream by <see cref="EmitAsync"/>.
        /// </summary>
        internal sealed class PreparedColumn : IDisposable {
            public ColumnChunk Chunk = null!;
            public MemoryStream Buffer = null!;

            public void Dispose() {
                Buffer?.Dispose();
            }
        }

        /// <summary>
        /// Encode and compress one column chunk into its own memory buffer. Touches no
        /// shared output-stream state, so many columns can be prepared CONCURRENTLY -
        /// which is the point: compression is the dominant cost of writing a parquet
        /// and it is a pure transform. The returned chunk carries placeholder offsets;
        /// <see cref="EmitAsync"/> rewrites them once the real stream position is known.
        /// </summary>
        internal async Task<PreparedColumn> PrepareAsync(
            FieldPath fullPath, DataColumn column,
            CancellationToken cancellationToken = default) {

            MemoryStream buffer = _rmsMgr.GetStream();

            // Created against the buffer, so the offsets it records are 0-based and
            // meaningless until EmitAsync fixes them up. Everything else it sets
            // (codec, type, path, encodings) is position-independent.
            // Num_values in the chunk does include null values - I have validated this by dumping spark-generated file.
            ColumnChunk chunk = _footer.CreateColumnChunk(
                _compressionMethod, buffer, _schemaElement.Type!.Value, fullPath, column.NumValues,
                _keyValueMetadata);

            ColumnSizes columnSizes = await WriteColumnAsync(
                buffer, chunk, column, _schemaElement,
                cancellationToken);
            //generate stats for column chunk
            chunk.MetaData!.Statistics = column.Statistics.ToThriftStatistics(_schemaElement);

            //the following counters must include both data size and header size
            chunk.MetaData.TotalCompressedSize = columnSizes.CompressedSize;
            chunk.MetaData.TotalUncompressedSize = columnSizes.UncompressedSize;

            buffer.Position = 0;
            return new PreparedColumn { Chunk = chunk, Buffer = buffer };
        }

        /// <summary>
        /// Append a prepared column chunk to the output stream. MUST run sequentially and
        /// in schema order: parquet records each chunk's absolute byte offset, so the
        /// position is only knowable at append time.
        /// </summary>
        internal async Task<ColumnChunk> EmitAsync(
            PreparedColumn prepared,
            CancellationToken cancellationToken = default) {

            long startPos = _stream.Position;
            prepared.Chunk.FileOffset = startPos;
            prepared.Chunk.MetaData!.DataPageOffset = startPos;
            await prepared.Buffer.CopyToAsync(_stream, 81920, cancellationToken);
            return prepared.Chunk;
        }

        class ColumnSizes {
            public int CompressedSize;
            public int UncompressedSize;
        }

        private async Task CompressAndWriteAsync(
            Stream target,
            PageHeader ph, MemoryStream data,
            ColumnSizes cs,
            CancellationToken cancellationToken) {

            using IronCompress.IronCompressResult compressedData = _compressionMethod == CompressionMethod.None
                ? new IronCompress.IronCompressResult(data.ToArray(), Codec.Snappy, false)
                : Compressor.Compress(_compressionMethod, data.ToArray(), _compressionLevel);

            ph.UncompressedPageSize = (int)data.Length;
            ph.CompressedPageSize = compressedData.AsSpan().Length;

            //write the header in
            using MemoryStream headerMs = _rmsMgr.GetStream();
            ph.Write(new Meta.Proto.ThriftCompactProtocolWriter(headerMs));
            int headerSize = (int)headerMs.Length;
            headerMs.Position = 0;
            target.Flush();

            await headerMs.CopyToAsync(target, 81920, cancellationToken);

            // write data
            target.WriteSpan(compressedData);

            cs.CompressedSize += headerSize;
            cs.UncompressedSize += headerSize;

            cs.CompressedSize += ph.CompressedPageSize;
            cs.UncompressedSize += ph.UncompressedPageSize;
        }

        private async Task<ColumnSizes> WriteColumnAsync(Stream target, ColumnChunk chunk, DataColumn column,
           SchemaElement tse,
           CancellationToken cancellationToken = default) {

            column.Field.EnsureAttachedToSchema(nameof(column));

            var r = new ColumnSizes();

            /*
             * Page header must preceeed actual data (compressed or not) however it contains both
             * the uncompressed and compressed data size which we don't know! This somehow limits
             * the write efficiency.
             */

            using var pc = new PackedColumn(column);
            pc.Pack(_options.UseDictionaryEncoding, _options.DictionaryEncodingThreshold);

            // dictionary page
            if(pc.HasDictionary) {
                PageHeader ph = _footer.CreateDictionaryPage(pc.Dictionary!.Length);
                using MemoryStream ms = _rmsMgr.GetStream();
                ParquetPlainEncoder.Encode(pc.Dictionary, 0, pc.Dictionary.Length,
                       tse,
                       ms, column.Statistics);

                await CompressAndWriteAsync(target, ph, ms, r, cancellationToken);
            }

            // data page
            using(MemoryStream ms = _rmsMgr.GetStream()) {
                bool deltaEncode = column.IsDeltaEncodable && _options.UseDeltaBinaryPackedEncoding;
                // data page Num_values also does include NULLs
                PageHeader ph = _footer.CreateDataPage(column.NumValues, pc.HasDictionary, deltaEncode);
                if(pc.HasRepetitionLevels) {
                    WriteLevels(ms, pc.RepetitionLevels!, pc.RepetitionLevels!.Length, column.Field.MaxRepetitionLevel);
                }
                if(pc.HasDefinitionLevels) {
                    WriteLevels(ms, pc.DefinitionLevels!, column.DefinitionLevels!.Length, column.Field.MaxDefinitionLevel);
                }

                if(pc.HasDictionary) {
                    // dictionary indexes are always encoded with RLE
                    int[] indexes = pc.GetDictionaryIndexes(out int indexesLength)!;
                    int bitWidth = pc.Dictionary!.Length.GetBitWidth();
                    ms.WriteByte((byte)bitWidth);   // bit width is stored as 1 byte before encoded data
                    RleBitpackedHybridEncoder.Encode(ms, indexes.AsSpan(0, indexesLength), bitWidth);
                } else {
                    Array data = pc.GetPlainData(out int offset, out int count);
                    if(deltaEncode) {
                        DeltaBinaryPackedEncoder.Encode(data, offset, count, ms, column.Statistics);
                        chunk.MetaData!.Encodings[2] = Encoding.DELTA_BINARY_PACKED;
                    } else {
                        ParquetPlainEncoder.Encode(data, offset, count, tse, ms, pc.HasDictionary ? null : column.Statistics);
                    }
                }

                ph.DataPageHeader!.Statistics = column.Statistics.ToThriftStatistics(tse);
                await CompressAndWriteAsync(target, ph, ms, r, cancellationToken);
            }

            return r;
        }

        private static void WriteLevels(Stream s, Span<int> levels, int count, int maxValue) {
            int bitWidth = maxValue.GetBitWidth();
            RleBitpackedHybridEncoder.EncodeWithLength(s, bitWidth, levels.Slice(0, count));
        }
    }
}
