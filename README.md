# SPT Co-op Installer

One-shot Windows installer for our Escape from Tarkov co-op nights: **SPT 4.1.6 + Fika 2.4.x co-op + a pinned 54-mod pack**, wired to the co-op backend.

Versions are pinned on purpose — everyone stays identical.

## Requirements

- Windows 10/11 x64 with a legitimate EFT install (BSG Launcher) — auto-detected from the registry or common paths
- Free space ≈ your EFT size + 25 GB on the target drive (checked before anything copies)
- Admin rights — the script self-elevates through the UAC prompt

## Install

1. Download `setup-spt.ps1` and `run-setup.bat` into one folder
2. Double-click **`run-setup.bat`** -> click **Yes** on the UAC prompt
   (if Windows says "Windows protected your PC": *More info* -> *Run anyway*)
3. Type an install path or press Enter (default `C:\SPT`)
4. Wait for the puns -> **`ALL DONE`** -> run `SPT_Runtime\SPT.Launcher.exe`, pick **Matt Co-op**, play

## Updating

Re-run with `-Update` to refresh the Fika + mod layer on an existing install from the pinned manifest:

```powershell
powershell -ExecutionPolicy Bypass -File setup-spt.ps1 -Update -InstallDir C:\SPT
```

## Notes

- Your live EFT install is never touched — the script copies it
- A downgrade patcher runs automatically when your EFT build is newer than the SPT target
- Tuned configs (bot caps, quest XP, Fika rules) survive re-runs
- The progress ticker makes Tarkov puns. This is mandatory. Cheeki breeki.

SPT and Fika are community projects — see their own sites for licensing and support.
