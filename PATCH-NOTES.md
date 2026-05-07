# Patch notes

This fork diverges from upstream `aloneguid/parquet-dotnet` 4.25.0 by exactly
two files:

* `src/Parquet/Meta/Proto/ThriftCompactProtocolReader.cs` — the substantive fix
* `src/Parquet/Parquet.csproj` — `<LangVersion>` pin to dodge a build break on
  newer .NET SDKs

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

## Upstream PR

Filed as **[aloneguid/parquet-dotnet#747](https://github.com/aloneguid/parquet-dotnet/pull/747)**
on 2026-05-07.

Scope of the upstream PR is the `ThriftCompactProtocolReader` change plus a
focused regression test in `src/Parquet.Test/ThriftTest.cs`. The
`Parquet.csproj` `<LangVersion>12</LangVersion>` pin is local-only (an
artifact of building under .NET 10 SDK, which resolves `latest` to C# 14
where `field` becomes a contextual keyword and breaks `StructField.cs`)
and is not upstreamed.

Once #747 lands and a release ships:

  * bump `pwiz_tools/Shared/Lib/Parquet/ParquetNet.dll` in pwiz to the stock
    upstream NuGet release
  * delete `pwiz_tools/OspreySharp/Directory.Build.targets`'s
    `OverridePatchedParquetNet` target (no longer needed)
  * archive or delete this fork (the `BinariesForProteoWizard/` artifacts
    can be removed; the `src/` source can stay or go depending on whether
    you want a reproducible record of what we built against)
