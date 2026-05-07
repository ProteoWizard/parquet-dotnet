# Building the Parquet.Net fork

This is the maccoss-developers fork of [aloneguid/parquet-dotnet](https://github.com/aloneguid/parquet-dotnet)
(MIT-licensed) at tag **4.25.0**, with a small patch to `ThriftCompactProtocolReader`
to fix a struct-skip bug that prevents reading parquet files emitted by
`parquet-rs >= 58` (the Rust crate used by `maccoss/osprey`). See `PATCH-NOTES.md`
for the patch summary.

## Output

A patched `Parquet.dll` is built and renamed to `ParquetNet.dll`, then copied to:

* `BinariesForProteoWizard/ParquetNet.{dll,pdb,xml}` — local artifact in this fork
* `pwiz_tools/Shared/Lib/Parquet/ParquetNet.dll` — consumed by Skyline and OspreySharp

This matches the existing fork pattern used by `DigitalRune-Docking-Windows`
elsewhere in this repo.

## Rebuilding

```pwsh
# From this directory:
dotnet build src/Parquet/Parquet.csproj -c Release `
    -p:NuGetAudit=false `
    -p:Version=4.25.0-osprey1 `
    -p:FileVersion=4.25.0 `
    -p:AssemblyVersion=4.0.0
```

Notes on the flags:

* `-p:NuGetAudit=false` — `Parquet.csproj` has `TreatWarningsAsErrors=true`, and a
  transitive `System.Text.Json 8.0.4` reference is flagged with a known-vulnerability
  audit warning. The osprey context only uses Parquet for column-blob round-tripping
  and never deserializes JSON via `System.Text.Json`, so the audit warning is not
  actionable for us. Disabling the audit lets the build complete without modifying
  upstream's package versions.
* `-p:AssemblyVersion=4.0.0` — must match upstream 4.25.0's `AssemblyVersion`
  (mapped to the major-only `4.0.0.0`) so that any binding redirects in
  consumer `app.config` files (`pwiz_tools/Skyline/app.config`, etc.) resolve to
  the patched dll without changes.
* `-p:FileVersion=4.25.0` — keeps the file-version field aligned with the upstream
  release so version inspection clearly identifies the source release the patch
  is built on top of.
* `-p:Version=4.25.0-osprey1` — distinguishes the patched build from a stock
  `Parquet.Net 4.25.0` NuGet package; bump the `-osprey<N>` suffix when applying
  additional patches.

## Deploying

After building, copy the four target outputs to `BinariesForProteoWizard/`:

```pwsh
Copy-Item src/Parquet/bin/Release/netstandard2.0/Parquet.dll `
    BinariesForProteoWizard/ParquetNet.dll
Copy-Item src/Parquet/bin/Release/netstandard2.0/Parquet.pdb `
    BinariesForProteoWizard/ParquetNet.pdb
Copy-Item src/Parquet/bin/Release/netstandard2.0/Parquet.xml `
    BinariesForProteoWizard/ParquetNet.xml
```

(`netstandard2.0` is the right TFM for both the Skyline net472 build and the
OspreySharp net472 / net8.0 builds since 4.25.0 doesn't ship a net472-specific
TFM and netstandard2.0 is the lowest common denominator.)

Then copy `BinariesForProteoWizard/ParquetNet.dll` (and `ParquetNet.xml`) into
`pwiz_tools/Shared/Lib/Parquet/` in your pwiz checkout for the consumers to
pick up.

## Upstream tracking

If upstream merges an equivalent fix, drop the patch and bump back to the stock
NuGet package. See `PATCH-NOTES.md` for the suggested upstream PR description.
