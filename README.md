# SPT Co-op Installer

Portable Windows installer for an SPT 4.1.6 co-op modpack. `SPTCoopSetup.exe` is a single
self-contained file: a .NET 10 WinForms host around a PowerShell 5.1 engine that installs
and updates the game layer, SPT, Fika and a pinned mod layer.

## Install

Run `SPTCoopSetup.exe`. There is no installer and no runtime to fetch: the app embeds
`setup-spt.ps1`, `install-mods.ps1` and `manifest/mods.json`, extracts them to a temp dir
and streams their output into the window. Three modes:

- Full install — copies Escape From Tarkov, applies the downgrade patch when the installed
  build differs, installs the SPT layer, Fika and the pinned mod layer, points the launcher
  at the co-op server and adds the firewall rules.
- Mods only — the mod layer onto an existing SPT install.
- Update — in place: game patch when the installed build differs, then a SPT/Fika/mods
  refresh. Never copies EFT, never deletes the user tree.

The install path defaults to `C:\SPT`. Automation, with no dialogs:

    SPTCoopSetup.exe --mode <full|mods|update> --path <dir> [--auto] [--preview-ui <path.png>] [--test-game <ticks>]

Prerequisites for a full install: Windows 10/11 x64, Escape From Tarkov installed through
the BSG launcher, the .NET 10 Desktop and ASP.NET Core 10 runtimes, and about 85 GB free
(~10 GB for an update). Fika P2P expects UDP 25565 reachable.

The engine is plain PowerShell and still runs without the app — `run-setup.bat` /
`run-mods.bat`, or (both scripts also take `-ArchiveDir`, `-ManifestPath`, `-NoElevate`):

    powershell -ExecutionPolicy Bypass -File setup-spt.ps1 [-InstallDir D:\SPT]
    powershell -ExecutionPolicy Bypass -File install-mods.ps1 [-SptDir D:\SPT]

Downloads are tried from the pinned upstream URLs first, then from an `archives\` folder
beside the exe (the USB hand-off); if both fail the run stops with one message naming the
asset and both locations. Each download stages to a `.part` file and moves into place only
once complete, so a failed run never leaves a half-installed tree.

## Build

Any OS with the .NET 10 SDK:

    dotnet publish src/SPTCoopSetup -c Release -r win-x64 --self-contained true \
      -p:PublishSingleFile=true -p:EnableWindowsTargeting=true \
      -p:IncludeNativeLibrariesForSelfExtract=true

## Manifest

`manifest/mods.json` is the pack: 52 entries pinned to SPT 4.1.6. Regenerate it with
`manifest/refresh.ps1` — `-Write` rewrites the json, `-Apply` also rewrites the pin blocks
inside the two scripts. Pins are chosen by `spt_version_constraint` accepting SPT 4.1.6,
newest of that track — never by publish date. The live-server diff is in `manifest/README.md`.

## Tests

`tests/test_localfetch.ps1` is the 11-case download/local-archive harness. On Windows run it
with the real `curl.exe`; on Linux run `bash tests/run-tests.sh`, which puts the documented
`curl.exe` shim on PATH. A passing run prints `LOCALFETCH-11/11`.

## Repo layout

- `setup-spt.ps1` — full install and update engine
- `install-mods.ps1` — mod layer onto an existing install
- `run-setup.bat`, `run-mods.bat` — launchers for the scripts above
- `src/SPTCoopSetup/` — WinForms app: CLI parsing, elevation, engine runner, UI
- `manifest/` — `mods.json` pack, `refresh.ps1`, live-server diff
- `tests/` — local-fetch harness, Linux runner and `curl.exe` shim
- `E2E-TEST-PROOF.md` — end-to-end test evidence
