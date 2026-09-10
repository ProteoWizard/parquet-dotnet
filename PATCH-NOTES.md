# Patch notes

This fork diverges from upstream `aloneguid/parquet-dotnet` 4.25.0 in five source
files, in two independent groups:

**Group 1 - the struct-skip correctness fix** (the original reason for the fork):

* `src/Parquet/Meta/Proto/ThriftCompactProtocolReader.cs` - the substantive fix
* `src/Parquet/Parquet.csproj` - `<LangVersion>` pin to dodge a build break on
  newer .NET SDKs

**Group 2 - parallel column compression on write** (added 2026-09-09):

* `src/Parquet/ParquetRowGroupWriter.cs` - the new public `WriteColumnsAsync` API
* `src/Parquet/File/DataColumnWriter.cs` - the Prepare/Emit split it needs
* `src/Parquet/File/Compressor.cs` - `ThreadLocal<Iron>`, a MISTAKEN determinism
  fix retained as a thread-safety precaution (see below - do not repeat its
  original rationale)
* `src/Parquet/Encodings/ParquetPlainEncoder.cs` - bool-encoder garbage byte
  (determinism)

Group 2 ADDS PUBLIC API, which Group 1 did not. That has a build consequence in
pwiz - see "Consuming the fork from pwiz" at the end.
## ThriftCompactProtocolReader: struct-skip fix + Double/Uuid cases

### Bug

`SkipField(CompactType.Struct)` reads each nested field's header but never
consumes the field's value. The original loop body is empty:

```csharp
case CompactType.Struct:
    StructBegin();
    while(ReadNextField(out _, out _)) {
        // BUG: empty body — reads field header, doesn't skip its value
    }
    StructEnd();
    break;
```

This works only when the unknown struct happens to consist entirely of `Stop`
bytes (i.e. an empty struct). For any non-trivial struct it leaves the read
cursor mis-aligned partway through the first nested field, so the next bytes
are interpreted as another field header. Sooner or later a random byte gets
parsed as compact-type 0x0D (`Uuid`), which falls through the switch's
`default` arm and throws `InvalidOperationException("don't know how to skip
type Uuid")`. The error message is a symptom of stream mis-alignment, not a
literal Uuid field on the wire.

### Trigger

The bug only fires when a parquet writer emits a struct-type field that
Parquet.Net's auto-generated `Read` methods don't know about — e.g. fields
added to `parquet-format.thrift` after Parquet.Net's last codegen sync. The
specific case that broke us was `parquet-rs >= 58` writing
`ColumnMetaData.size_statistics` (field 16, struct), which Parquet.Net 4.25.0's
`ColumnMetaData.Read` does not enumerate, so it falls back to
`SkipField(CompactType.Struct)`.

The same bug is still present in upstream 5.6.1 and 6.0.1 as of 2026-05-07.

### Fix

```csharp
case CompactType.Struct:
    StructBegin();
    while(ReadNextField(out _, out CompactType nestedType)) {
        SkipField(nestedType);
    }
    StructEnd();
    break;
```

While in the same switch, two adjacent cases that were commented out are also
implemented:

* `CompactType.Double` (compact type 7) — skip 8 bytes
* `CompactType.Uuid` (compact type 13, the genuine Apache Thrift Uuid type) —
  skip 16 bytes per the compact-protocol spec

The Double case in particular matters because the `default` branch's
`InvalidOperationException` would otherwise fire on a future spec addition that
embeds a struct containing a Double-typed field.

## Parquet.csproj: LangVersion pin

`<LangVersion>latest</LangVersion>` resolves to C# 14 under the .NET 10 SDK.
C# 14 turns `field` into a contextual keyword, which breaks the existing
`StructField.cs` member named `field`. Pinning to `<LangVersion>12</LangVersion>`
keeps the upstream sources buildable without touching the field names.


## Parallel column compression on write

### Why

Osprey writes ~164 GB of parquet in an 82-file SEA-AD run and the write was
single-threaded: `ParquetRowGroupWriter` compressed and emitted one column at a
time. Compression is the cost and the columns of a row group are independent, so
it parallelises cleanly - but only if the file bytes stay identical, because the
Osprey regression goldens compare output.

### The API

```csharp
public async Task WriteColumnsAsync(IReadOnlyList<DataColumn> columns,
    Dictionary<string, string>? customMetadata = null,
    int maxDegreeOfParallelism = 0,
    CancellationToken cancellationToken = default)
```

`maxDegreeOfParallelism` 0 means `Environment.ProcessorCount`; 1 forces the
sequential loop *through the same code path*, which is how the A/B against the
golden is taken. `WriteColumnAsync` is untouched.

`DataColumnWriter` is split into `PrepareAsync` (encode + compress into a
`MemoryStream`, no I/O, safe to run concurrently) and `EmitAsync` (append to the
output stream). Parquet records each chunk's ABSOLUTE offset, so the emit loop
stays strictly ordered and single-threaded; only Prepare runs under
`Parallel.For`. A failed Prepare disposes the prepared columns that did complete,
or a failed write would leak the whole row group's pooled buffers.

### The determinism bug this exposed - and one wrong turn on the way

Parallelising the compression made the output non-reproducible: three runs over the
same input produced three different files, sizes differing by a few bytes on small,
highly-compressible chunks. Decompressed values were identical every time, but
parquet output has to be reproducible - it is what the regression goldens compare.

**The wrong turn: `ThreadLocal<Iron>`.** The first hypothesis was that `Compressor`
sharing one `Iron` across threads carried codec state between them. `Compressor.cs`
was changed to `ThreadLocal<Iron>` - and it **did not fix anything**. The commit that
introduced it (915984a) says so in its own message: "Making Iron thread-local did NOT
fix it." It was never reverted, and for a while both the code comment and this file
described it as the determinism fix. It is not one.

What it is now: a cheap thread-safety precaution, since IronCompress makes no
documented thread-safety guarantee and `Compress` now runs concurrently. It costs one
allocation per worker thread. **Do not report it upstream as a parquet-dotnet bug** -
there is no evidence of one. Note it also routes `Decompress` through the ThreadLocal,
so it touches the READ path of a binary Skyline ships, and the `ThreadLocal` is never
disposed. Reverting it is a live option (see "Open question" below).

**The actual cause** was ours, not upstream's codec.

**`ParquetPlainEncoder.Encode(ReadOnlySpan<bool>, ...)` wrote an uninitialised
byte.** `targetLength` was `(data.Length / 8) + 1`, which over-counts by a whole
byte when the length is a multiple of 8. That extra byte was never written by the
packing loop - it only stores the buffer slot when a PARTIAL byte is left over -
and `ArrayPool.Rent()` returns uncleared memory, so the trailing byte in every
such bool page was whatever the pool happened to hold.

Readers ignore bits past the value count, so the data always round-tripped and
the bug stayed invisible. But it is uninitialised heap written into the file, and
the pool's reuse pattern is deterministic single-threaded (which is why goldens
were stable) and NOT across threads. Fixed to `(data.Length + 7) / 8`.

Found by noticing that `is_decoy` - the only bool column - was the only column
whose compressed size varied run to run. Fixing it made the output reproducible:
932ade5 verified sequential and two parallel runs all producing sha 87E505583C14.

**Consequence for consumers:** bool column pages whose row count is a multiple of
8 lose a trailing garbage byte. Byte-for-byte output changes; decoded values do
not. Anything asserting on parquet BYTES (rather than values) will see a diff.

### Open question: keep or revert `ThreadLocal<Iron>`?

Unresolved, and it is a judgement call rather than a missing measurement.

* **Keep** - zero rebuild, and the binary the 82-file benchmark measured stays the
  one being shipped. Cost: an undocumented-in-upstream-terms change to the
  decompression path of a DLL Skyline ships, plus a `ThreadLocal` that is never
  disposed and so retains one `Iron` per thread that ever touches it.
* **Revert** - smaller divergence, and nothing in the record shows it does any good.
  Cost: a fork rebuild, a re-stage into pwiz, and a re-run of the Astral gate; and
  the shipped binary would then no longer be the one the benchmark measured.

There is no evidence either way on whether `Iron` is safe to share across threads -
only evidence that sharing it was NOT the cause of the byte drift. Reverting is
therefore not provably safe, merely smaller. Decide deliberately.

## Consuming the fork from pwiz

`pwiz_tools/Osprey/Directory.Build.targets` has an `OverridePatchedParquetNet`
target that copies `pwiz_tools/Shared/Lib/Parquet/ParquetNet.dll` over the
NuGet-resolved `Parquet.dll` in the output directory AFTER build. That is enough
for a patch that only changes BEHAVIOUR - the compiler binds against the stock
4.25.0 reference assembly and the fork's binary answers at run time.

It is NOT enough once the fork ADDS public API. `WriteColumnsAsync` does not exist
on the stock reference assembly, so `Osprey.IO` failed to compile with CS1061 even
though the right binary was in the output. `Osprey.IO.csproj` therefore does both:

```xml
<PackageReference Include="Parquet.Net" Version="4.25.0" ExcludeAssets="compile" />
<Reference Include="Parquet">
  <HintPath>..\..\Shared\Lib\Parquet\ParquetNet.dll</HintPath>
</Reference>
```

`ExcludeAssets="compile"` keeps the package for its transitive dependencies
(IronCompress and friends) while removing its reference assembly from the compile
closure; the direct `<Reference>` then supplies the fork's surface at compile time
as well as run time. This is the same shape `Skyline.csproj` already used.

**Ship the `netstandard2.0` build.** Skyline is still net472 and references the
same `Shared/Lib/Parquet/ParquetNet.dll` directly, so one binary serves both. A
net8.0-only build would break Skyline.

Rebuild and stage with the commands in the "Rebuilding the fork" section of the
Osprey handoff notes - a `Release` build of `src/Parquet/Parquet.csproj` pinned to
`-p:Version=4.25.0-osprey2 -p:FileVersion=4.25.0 -p:AssemblyVersion=4.0.0`, then
copy `src/Parquet/bin/Release/netstandard2.0/Parquet.dll` to BOTH
`<pwiz>\pwiz_tools\Shared\Lib\Parquet\ParquetNet.dll` and
`BinariesForProteoWizard\ParquetNet.dll`.

## Upstream PR

The struct-skip fix is filed as
**[aloneguid/parquet-dotnet#747](https://github.com/aloneguid/parquet-dotnet/pull/747)**
(2026-05-07). Scope is the `ThriftCompactProtocolReader` change plus a focused
regression test in `src/Parquet.Test/ThriftTest.cs`.

Not upstreamed:
* the `Parquet.csproj` `<LangVersion>12</LangVersion>` pin - local build artifact
* the parallel-write work - not yet offered upstream. The **bool-encoder byte
  count** IS a genuine upstream bug in its own right and is worth filing separately
  from the API addition: it writes uninitialised heap into every affected page
  regardless of threading. (Upstream 6.1.0 already carries the same fix
  independently, so check before filing.)
* the `ThreadLocal<Iron>` change is **NOT** an upstream bug report. See above - it
  fixed nothing and is kept only as a precaution.

**Retiring the fork is no longer just "wait for #747".** When #747 lands, the
struct-skip reason goes away, but `WriteColumnsAsync` does not exist upstream, so
moving to a stock release means either upstreaming the parallel write or giving it
up. Track those as two separate decisions.

If both are ever resolved upstream:

  * bump `pwiz_tools/Shared/Lib/Parquet/ParquetNet.dll` to the stock upstream release
  * revert `Osprey.IO.csproj` to a plain `<PackageReference>` (drop
    `ExcludeAssets="compile"` and the direct `<Reference>`)
  * delete the `OverridePatchedParquetNet` target in
    `pwiz_tools/Osprey/Directory.Build.targets`
  * archive or delete this fork
