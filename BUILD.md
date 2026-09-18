# Building the Parquet.Net fork

This is the `pwiz/4.25.0` branch of the ProteoWizard fork of
[aloneguid/parquet-dotnet](https://github.com/aloneguid/parquet-dotnet)
(MIT-licensed): tag **4.25.0** plus a small patch to `ThriftCompactProtocolReader`
to fix a struct-skip bug that prevents reading parquet files emitted by
`parquet-rs >= 58` (the Rust crate used by `maccoss/osprey`). See `PATCH-NOTES.md`
for the patch summary.

## Output

A patched `Parquet.dll` is built and renamed to `ParquetNet.dll`, then committed to
the pwiz repo as `pwiz_tools/Shared/Lib/Parquet/ParquetNet.dll`, where Skyline and
Osprey consume it. Build outputs are not committed to this fork; the
`-p:Version` suffix and the git SHA embedded in the dll's informational version
(`4.25.0-osprey<N>+<sha>`) tie a shipped binary back to the commit it was built from.

## CI builds (the binary to ship)

`.github/workflows/pwiz-binaries.yml` runs on every push to a non-`master` branch
and on pull requests into one. It runs the unit tests on Windows and Ubuntu, builds
`Parquet.csproj` in Release with the flags below, and uploads a
`ParquetNet-<version>` artifact holding `ParquetNet.dll`, `ParquetNet.pdb`,
`ParquetNet.xml`, the `.nupkg`, and a `version.txt` with the exact
`<version>+<sha>` it was built from.

Prefer the CI artifact over a local build for anything that ships. CI stamps
`src/Parquet/Globals.cs` (as upstream's own CI does) so `Globals.Version` and
`Globals.GithubSha` are real values; a local build leaves the literal
`${VERSION}` / `${GITHUB_SHA}` placeholders, and they end up in the `created_by`
field of every parquet file the library writes.

The version is the single `VERSION` + `PACKAGE_SUFFIX` pair at the top of the
workflow. To ship a new patch:

1. Bump `PACKAGE_SUFFIX` (`-osprey<N>`) in the workflow and commit it with the patch.
2. Push a tag named exactly `<VERSION><PACKAGE_SUFFIX>`, e.g. `4.25.0-osprey3`.
   The build job refuses a tag that does not match the workflow's version, and a
   matching tag additionally creates a GitHub Release with the same files attached.
3. Copy `ParquetNet.dll` and `ParquetNet.xml` from the artifact or release into
   `pwiz_tools/Shared/Lib/Parquet/` in pwiz and commit them there.

## Rebuilding locally

```pwsh
# From this directory:
dotnet build src/Parquet/Parquet.csproj -c Release `
    -p:NuGetAudit=false `
    -p:Version=4.25.0-osprey3 `
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
* `-p:Version=4.25.0-osprey3` — distinguishes the patched build from a stock
  `Parquet.Net 4.25.0` NuGet package; bump the `-osprey<N>` suffix when applying
  additional patches, and keep it in step with `PACKAGE_SUFFIX` in the workflow.

## Deploying a local build

For local iteration only (see "CI builds" above for what to ship), copy the
`netstandard2.0` outputs into your pwiz checkout:

```pwsh
Copy-Item src/Parquet/bin/Release/netstandard2.0/Parquet.dll `
    <pwiz>/pwiz_tools/Shared/Lib/Parquet/ParquetNet.dll
Copy-Item src/Parquet/bin/Release/netstandard2.0/Parquet.xml `
    <pwiz>/pwiz_tools/Shared/Lib/Parquet/ParquetNet.xml
```

(`netstandard2.0` is the right TFM for both the Skyline net472 build and the
OspreySharp net472 / net8.0 builds since 4.25.0 doesn't ship a net472-specific
TFM and netstandard2.0 is the lowest common denominator.)

Commit the copied files in pwiz for the consumers to pick up.

## Upstream tracking

If upstream merges an equivalent fix, drop the patch and bump back to the stock
NuGet package. See `PATCH-NOTES.md` for the suggested upstream PR description.
