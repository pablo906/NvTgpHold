# Building from source

## Prerequisites

Use Windows x64 and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
to build and package the Windows Forms application. Running it also requires the
hardware/drivers described in [Installation](Installation.md). Building and running
unit tests do not require administrator rights or access to EC hardware.

```powershell
git clone https://github.com/pablo906/NvTgpHold.git
cd NvTgpHold
dotnet build -c Release
dotnet test tests/NvTgpHold.Tests -c Release
```

The application output is in `bin/Release/`. Keep the complete output together,
including `LpcACPIEC.bin` and `RyzenSMU.bin`. The modules' provenance and license are in
[third_party/PawnIO.Modules.md](../third_party/PawnIO.Modules.md).

The hardware-free test project targets plain .NET 9 and can run on Linux/macOS
with a suitable SDK. It covers power arithmetic, profiles, settings, configuration
parsing and EC target gates. Passing tests does not validate undocumented driver
calls, safe power levels or EC transport behavior on hardware.

## Documentation maintenance

Edit the Markdown files in `docs/` in the same PR as behavior changes. Keep setup
and usage task-oriented; put firmware addresses and research evidence in
`EC-Ladder.md`. Identify untested behavior and distinguish observations from guarantees.
The root README provides the overview and entry points rather than duplicating the
full menu reference.

The wiki is a published mirror. Clone it and generate its pages from the reviewed
documentation; the script converts relative repository links for GitHub Wiki:

```powershell
git clone https://github.com/pablo906/NvTgpHold.wiki.git ..\NvTgpHold.wiki
.\tools\Sync-Wiki.ps1 -WikiPath ..\NvTgpHold.wiki
git -C ..\NvTgpHold.wiki diff --check
git -C ..\NvTgpHold.wiki diff
```

Commit and push the generated pages after the source documentation is merged.
Do not maintain a separate set of wiki instructions by hand.

## Release checklist

1. Update `<Version>` in `NvTgpHold.csproj` and version-specific download examples.
2. Build Release, run the tests and check Markdown links, wiki output and packaging.
3. Publish framework-dependent Windows x64 output:

   ```powershell
   dotnet publish NvTgpHold.csproj -c Release -r win-x64 --self-contained false -o publish
   ```

4. Package the published application files with `LpcACPIEC.bin`, `RyzenSMU.bin`, the MIT license,
   `third_party/` license/provenance notices and `docs/`. Exclude symbols and temporary files.
   Include the LGPL module's matching source archive under `third_party/`, as
   identified in its provenance notice.
5. Merge through a reviewed PR, tag the merged commit, and publish
   `NvTgpHold-vX.Y.Z-win-x64.zip` with a SHA-256 checksum.
6. Publish the wiki mirror and release notes describing user-visible changes,
   upgrade considerations and validation limits. Verify the asset, tag and wiki
   after publication. Do not describe software-only checks as hardware validation.
