using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.File;
using Parquet.Meta;
using Parquet.Schema;
using FieldPath = Parquet.Schema.FieldPath;

namespace Parquet {
    /// <summary>
    /// Writer for Parquet row groups
    /// </summary>
#pragma warning disable CA1063 // Implement IDisposable Correctly
    public class ParquetRowGroupWriter : IDisposable
#pragma warning restore CA1063 // Implement IDisposable Correctly
    {
        private readonly ParquetSchema _schema;
        private readonly Stream _stream;
        private readonly ThriftFooter _footer;
        private readonly CompressionMethod _compressionMethod;
        private readonly CompressionLevel _compressionLevel;
        private readonly ParquetOptions _formatOptions;
        private readonly RowGroup _owGroup;
        private readonly SchemaElement[] _thschema;
        private int _colIdx;

        internal ParquetRowGroupWriter(ParquetSchema schema,
           Stream stream,
           ThriftFooter footer,
           CompressionMethod compressionMethod,
           ParquetOptions formatOptions,
           CompressionLevel compressionLevel) {
            _schema = schema ?? throw new ArgumentNullException(nameof(schema));
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _footer = footer ?? throw new ArgumentNullException(nameof(footer));
            _compressionMethod = compressionMethod;
            _compressionLevel = compressionLevel;
            _formatOptions = formatOptions;

            _owGroup = _footer.AddRowGroup();
            _owGroup.Columns = new List<ColumnChunk>();
            _thschema = _footer.GetWriteableSchema();
        }

        internal long? RowCount { get; private set; }

        /// <summary>
        /// Writes next data column to parquet stream. Note that columns must be written in the order they are declared in the
        /// file schema.
        /// </summary>
        /// <param name="column"></param>
        /// <param name="cancellationToken"></param>
        public Task WriteColumnAsync(DataColumn column, CancellationToken cancellationToken = default) {
            return WriteColumnAsync(column, null, cancellationToken);
        }

        /// <summary>
        /// Writes next data column to parquet stream. Note that columns must be written in the order they are declared in the
        /// file schema.
        /// </summary>
        /// <param name="column"></param>
        /// <param name="customMetadata">If specified, adds custom column chunk metadata</param>
        /// <param name="cancellationToken"></param>
        public async Task WriteColumnAsync(DataColumn column,
            Dictionary<string, string>? customMetadata,
            CancellationToken cancellationToken = default) {
            if(column == null)
                throw new ArgumentNullException(nameof(column));

            if(RowCount == null) {
                if(column.NumValues > 0 || column.Field.MaxRepetitionLevel == 0)
                    RowCount = column.CalculateRowCount();
            }

            SchemaElement tse = _thschema[_colIdx];
            if(!column.Field.Equals(tse)) {
                throw new ArgumentException($"cannot write this column, expected '{tse.Name}', passed: '{column.Field.Name}'", nameof(column));
            }
            _colIdx += 1;

            FieldPath path = _footer.GetPath(tse);

            var writer = new DataColumnWriter(_stream, _footer, tse,
               _compressionMethod,
               _formatOptions,
               _compressionLevel,
               customMetadata);

            ColumnChunk chunk = await writer.WriteAsync(path, column, cancellationToken);
            _owGroup.Columns.Add(chunk);

        }

        /// <summary>
        /// Writes several columns at once, compressing them CONCURRENTLY and appending them
        /// in the order given. Byte-for-byte equivalent to calling
        /// <see cref="WriteColumnAsync(DataColumn, Dictionary{string, string}, CancellationToken)"/>
        /// once per column in the same order - compression is a pure transform, and only the
        /// append has to be ordered - but compression, which dominates the cost of writing a
        /// parquet, runs across columns instead of one at a time.
        /// </summary>
        /// <param name="columns">Columns in schema order.</param>
        /// <param name="customMetadata">Optional key/value metadata, applied to every column.</param>
        /// <param name="maxDegreeOfParallelism">Concurrent compressions; 0 or less means
        /// <see cref="Environment.ProcessorCount"/>. Memory scales with this: each in-flight
        /// column holds its compressed chunk until it is appended.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task WriteColumnsAsync(IReadOnlyList<DataColumn> columns,
            Dictionary<string, string>? customMetadata = null,
            int maxDegreeOfParallelism = 0,
            CancellationToken cancellationToken = default) {

            if(columns == null)
                throw new ArgumentNullException(nameof(columns));
            if(columns.Count == 0)
                return;

            // Schema validation, column index and row count are shared writer state, so they
            // are settled here, sequentially, before anything runs concurrently.
            var writers = new DataColumnWriter[columns.Count];
            var paths = new FieldPath[columns.Count];
            for(int i = 0; i < columns.Count; i++) {
                DataColumn column = columns[i];
                if(column == null)
                    throw new ArgumentNullException(nameof(columns), $"column at index {i} is null");

                if(RowCount == null) {
                    if(column.NumValues > 0 || column.Field.MaxRepetitionLevel == 0)
                        RowCount = column.CalculateRowCount();
                }

                SchemaElement tse = _thschema[_colIdx];
                if(!column.Field.Equals(tse)) {
                    throw new ArgumentException($"cannot write this column, expected '{tse.Name}', passed: '{column.Field.Name}'", nameof(columns));
                }
                _colIdx += 1;

                paths[i] = _footer.GetPath(tse);
                writers[i] = new DataColumnWriter(_stream, _footer, tse,
                   _compressionMethod,
                   _formatOptions,
                   _compressionLevel,
                   customMetadata);
            }

            int dop = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
            dop = Math.Min(dop, columns.Count);

            // Both branches run the SAME PrepareAsync per column and differ only in the loop,
            // so a sequential-vs-parallel comparison isolates concurrency and nothing else.
            // PrepareAsync does no real I/O (it writes to a MemoryStream), so blocking on it
            // here costs nothing and keeps Task scheduling out of the picture.
            var prepared = new DataColumnWriter.PreparedColumn[columns.Count];
            try {
                if(dop <= 1) {
                    for(int i = 0; i < columns.Count; i++)
                        prepared[i] = PrepareOne(writers, paths, columns, i, cancellationToken);
                } else {
                    var po = new ParallelOptions {
                        MaxDegreeOfParallelism = dop,
                        CancellationToken = cancellationToken
                    };
                    Parallel.For(0, columns.Count, po,
                        i => prepared[i] = PrepareOne(writers, paths, columns, i, cancellationToken));
                }
            } catch {
                // Whatever did complete still owns pooled buffers; release them before the
                // exception leaves, or a failed write leaks the whole row group.
                foreach(DataColumnWriter.PreparedColumn p in prepared)
                    p?.Dispose();
                throw;
            }

            // Ordered append: parquet records each chunk's absolute offset, so this cannot
            // be reordered or parallelised.
            for(int i = 0; i < columns.Count; i++) {
                using(DataColumnWriter.PreparedColumn p = prepared[i]) {
                    ColumnChunk chunk = await writers[i].EmitAsync(p, cancellationToken);
                    _owGroup.Columns.Add(chunk);
                }
            }
        }

        private static DataColumnWriter.PreparedColumn PrepareOne(
            DataColumnWriter[] writers, FieldPath[] paths, IReadOnlyList<DataColumn> columns,
            int i, CancellationToken cancellationToken) {
            return writers[i].PrepareAsync(paths[i], columns[i], cancellationToken)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 
        /// </summary>
#pragma warning disable CA1063 // Implement IDisposable Correctly
        public void Dispose()
#pragma warning restore CA1063 // Implement IDisposable Correctly
        {
            //todo: check if all columns are present

            //row count is know only after at least one column is written
            _owGroup.NumRows = RowCount ?? 0;

            //row group's size is a sum of _uncompressed_ sizes of all columns in it, including the headers
            //luckily ColumnChunk already contains sizes of page+header in it's meta
            _owGroup.TotalByteSize = _owGroup.Columns.Sum(c => c.MetaData!.TotalCompressedSize);
        }
    }
}