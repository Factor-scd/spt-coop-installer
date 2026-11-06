#Requires -Version 5.1
<#
.SYNOPSIS
  SPT + FIKA + modpack one-shot installer for Matt's co-op crew (Windows 10/11 x64).
.DESCRIPTION
  Replicates the official SPT installer non-interactively:
    EFT copy -> optional downgrade patcher -> SPT 4.1.6 layer -> Fika 2.4.x -> 54 pinned mods
    -> launcher pointed at the co-op server -> firewall rules -> verify.

  Assumes Escape From Tarkov IS installed (BSG Launcher, you must own the game). The script
  locates it via registry + common paths (override with -EftPath) and stops with a clear
  message if it cannot find it - it does not install or repair EFT itself.

  -Update refreshes the Fika + mod layer over an existing SPT install from the PINNED
  manifest (version bumps arrive with a re-pinned script - everyone staying identical is
  the point). It skips EFT detection, copying and the patcher.

  Run with no arguments and the script asks where SPT should live (Enter accepts
  C:\SPT). Pass -InstallDir to skip the prompt (automation / -Update runs).

  Prereqs (checked up front):
    - Escape From Tarkov installed via BSG Launcher [not needed for -Update]
    - .NET 10 Desktop + ASP.NET Core 10 runtimes
    - EFT size + ~25 GB free for a fresh install (~85 GB for a standard EFT) / ~10 GB for -Update
    - Admin PowerShell

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File setup-spt.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File setup-spt.ps1 -InstallDir D:\SPT -ServerHost 10.4.20.2:6969
#>
param(
  [string]$InstallDir = "C:\SPT",
  [string]$ServerHost = "10.4.20.2:6969",
  [string]$EftPath = "",
  [switch]$Update
)

$ErrorActionPreference = "Stop"
$targetEft   = "0.16.9.5.40743"
$targetBuild = "40743"
$spt7z       = "https://github.com/SP-Tushonka/build/releases/download/4.1.6/SPT-4.1.6-40743-731d7a2.7z"
$fikaPlugin  = "https://github.com/project-fika/Fika-Plugin/releases/download/v2.4.6/Fika.Release.2.4.6.zip"
$fikaServer  = "https://github.com/project-fika/Fika-Server-CSharp/releases/download/v2.4.2/Fika.Server.Release.2.4.2.zip"
$sevenZipR   = "https://www.7-zip.org/a/7zr.exe"
$patchHosts  = @("https://patcher.sp-tushonka.com", "https://mirror.sp-tushonka.com")

# id|kind|slug|version   (kind: mod|addon)
$ModManifest = @(
  "2512|mod|wtt-content-backport|2.0.5",
  "2310|mod|wtt-commonlib|3.0.6",
  "1342|mod|ui-fixes|6.0.4",
  "1358|mod|brightlasers|3.0.0",
  "1884|mod|continuous-healing|1.6.3",
  "1469|mod|autodeposit|6.0.0",
  "2207|mod|fast-sell-in-flea|1.3.1",
  "236|mod|server-value-modifier-svm|2.2.3",
  "2025|mod|all-quests-checkmarks|1.4.0",
  "2097|mod|abps-acids-bot-placement-system|2.1.0",
  "1621|mod|shadow-flicker-fix|1.5.3",
  "2314|mod|season-rotator|2.0.1",
  "2112|mod|continuous-load-ammo|1.2.0",
  "2386|mod|use-items-anywhere|2.1.6",
  "1657|mod|mergeconsumables|1.6.3",
  "3103|mod|cabinet-looter|1.0.4",
  "2003|mod|hollywoodfx|3.0.0",
  "1298|mod|handsarenotbusy|1.7.1",
  "167|mod|ammo-stats|4.1.0",
  "1343|mod|skipper|1.1.5",
  "2790|mod|easy-mounting|2.0.1",
  "2734|mod|better-night-skies|2.0.0",
  "1431|mod|dynamic-maps|1.2.1",
  "2540|mod|content-backport-prestiges|1.1.0",
  "2689|mod|item-property-backport|1.5.2",
  "2866|mod|manimals-icebreaker-backport|1.1.3",
  "3179|mod|interface-11-backport|1.0.4",
  "3211|mod|server-room-backport|1.0.0",
  "3040|mod|map-variants|1.1.0",
  "3193|mod|tushonka-mapping-sdk|1.9.3",
  "2714|mod|discord-raid-map|2.0.0",
  "2049|mod|add-secret-exfil-notes-to-caches|2.1.0",
  "1888|mod|better-keys-ng|1.8.0",
  "3047|mod|deploy-screen-overhaul|1.1.0",
  "98|addon|manimals-icebreaker-backport-fika-sync|1.1.0",
  "902|mod|bigbrain|1.5.0",
  "2426|mod|morebotsapi|2.1.1",
  "2511|mod|wtt-black-division-redacted-home|1.3.1",
  "2649|mod|climbable-ladders|1.0.4",
  "2864|mod|manimals-cs-gas|2.0.0",
  "3025|mod|manimals-10-interchange-expansion-backport|1.0.5",
  "1426|mod|unitytoolkit|2.0.2",
  "791|mod|sain-solarints-ai-modifications-full-ai-combat-system-replacement|4.5.1",
  "827|mod|waypoints-expanded-navmesh|1.9.0",
  "3236|mod|return-of-thunderstorms|1.2.4",
  "2153|mod|expanded-task-text-ett|2.1.0",
  "2639|mod|auto-shoulder-swap|1.1.1",
  "2681|mod|manimals-ammo-loading-animations|2.0.0",
  "1374|mod|player-encumbrance-bar|1.3.0",
  "2954|mod|quest-marker|1.4.0",
  "3214|mod|quest-glow|1.0.0",
  "2971|mod|universal-co-op-exfil|1.0.1",
  "2125|mod|dehazardifier-updated-by-tetris|1.2.0",
  "2879|mod|weboverlay|1.11.0"
)

function Say($m, $c = "White") { Write-Host ">> $m" -ForegroundColor $c }
function Die($m) { Write-Host "SETUP FAILED: $m" -ForegroundColor Red; exit 1 }

# ---------- silly Tarkov pun ticker (runs during long waits) ----------
$puns = @(
  "Found in raid: your patience.",
  "Copying gigabytes... that's a lot of cheeki breeki.",
  "Still faster than a hatchling run to D2.",
  "Head, eyes? No - head, sighs.",
  "Jaeger quest: survive this download. Reward: -0.01 rep.",
  "Insurance won't bring this time back.",
  "Killed by: LoadingBar (Thorax).",
  "Extract camping this progress bar.",
  "Even the sniper scav gave up waiting.",
  "Stamina 0. Weight: all of it.",
  "The flea market sells faster, but the fees are brutal.",
  "Tagilla wants his hammer back. Let him wait.",
  "Cultists spawn at night. This install spawns always.",
  "Therapist can't heal this boredom.",
  "Prapor said 2 hours. It's been 3.",
  "SAIN bots path better than this download.",
  "Hideout upgrade complete: +1 patience regen.",
  "Zryachiy guards this download. Do not approach.",
  "Leg meta? Nah, leg day.",
  "GPU found in raid... just kidding, more archives.",
  "Scav case: random junk in, puns out.",
  "Extracting... no, not that kind of extract.",
  "That Reserve raid with 28 bots was quicker.",
  "Moonshine for the wait? Don't mind if I do.",
  "Peacekeeper wants USD. We accept GB.",
  "Fog of war? Fog of download.",
  "One-tapped by the patcher.",
  "Your PMC is tired. So is the disk.",
  "The real raid was the bytes we copied along the way.",
  "Cheeki breeki, iv damke!"
)
$script:tickPs = $null
function Start-Ticker($label, $every = 15) {
  $script:tickPs = [powershell]::Create()
  [void]$script:tickPs.AddScript({
    param($puns, $label, $every)
    $i = 0
    $spin = @('|', '/', '-', '\')
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
      Start-Sleep -Seconds $every
      $t = $sw.Elapsed.ToString('mm\:ss')
      # parens REQUIRED: inside method args a bare comma list splits into separate method args
      [Console]::WriteLine(("  {0} [{1}] {2}  --  {3}" -f $spin[$i % 4], $t, $label, $puns[$i % $puns.Count]))
      $i++
    }
  }).AddArgument($puns).AddArgument($label).AddArgument($every)
  [void]$script:tickPs.BeginInvoke()
}
function Stop-Ticker {
  if ($script:tickPs) { $script:tickPs.Stop(); $script:tickPs.Dispose(); $script:tickPs = $null }
}

# ---------- 0. preflight ----------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole("Administrators")) {
  Say "needs an admin shell - asking Windows to elevate (click Yes on the UAC prompt)..."
  try {
    Start-Process -FilePath "powershell.exe" -Verb RunAs -Wait -ArgumentList @(
      '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath)
    ) | Out-Null
    exit 0
  } catch {
    Die "elevation declined. Re-run from an ADMIN PowerShell: press Win+X -> Windows PowerShell (Admin), then: powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`""
  }
}

# install path: ask up front unless -InstallDir was passed (Enter accepts the default)
if (-not $PSBoundParameters.ContainsKey('InstallDir')) {
  while ($true) {
    $answer = "$(Read-Host "  SPT install path [$InstallDir]")".Trim().Trim('"')
    if ($answer -eq "") { break }
    if ($answer.Length -gt 3) { $answer = $answer.TrimEnd('\') }
    if ($answer -match '^[A-Za-z]:') { $InstallDir = $answer; break }
    Say "  need a drive-rooted path like D:\SPT (or just Enter for $InstallDir)" "Yellow"
  }
}
Say "SPT install path: $InstallDir" "Green"

$drive = (Get-Item (Split-Path -Qualifier $InstallDir)).PSDrive
# free-space gate runs after EFT detection (fresh need = EFT size + headroom, measured there)

# .NET 10 runtimes
$dotnetShared = "C:\Program Files\dotnet\shared"
$hasDesktop = Test-Path "$dotnetShared\Microsoft.WindowsDesktop.App\10.*"
$hasAsp     = Test-Path "$dotnetShared\Microsoft.AspNetCore.App\10.*"
if (-not ($hasDesktop -and $hasAsp)) {
  Say "Missing .NET 10 runtimes - install BOTH, then re-run:" "Yellow"
  Say "  https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-10.0.9-windows-x64-installer"
  Say "  https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-aspnetcore-10.0.9-windows-x64-installer"
  Die ".NET 10 Desktop + ASP.NET Core 10 (10.0.9+) required."
}

# ---------- 1. locate EFT (assumed installed; skipped in -Update mode) ----------
$eft = $null
$eftBuild = ""
if (-not $Update) {
  function Find-Eft {
    if ($EftPath -ne "") {
      if ((Test-Path "$EftPath\EscapeFromTarkov.exe") -and (Test-Path "$EftPath\EscapeFromTarkov_Data")) { return $EftPath }
      return $null
    }
    $candidates = @()
    foreach ($reg in @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                       "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")) {
      Get-ChildItem $reg -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($p.DisplayName -like "*Escape From Tarkov*" -and $p.InstallLocation) { $candidates += $p.InstallLocation }
      }
    }
    $candidates += "C:\Battlestate Games\EFT"
    $candidates += "D:\Battlestate Games\EFT"
    $candidates += "C:\Program Files (x86)\Steam\steamapps\common\Escape from Tarkov"
    foreach ($c in ($candidates | Select-Object -Unique)) {
      if ((Test-Path "$c\EscapeFromTarkov.exe") -and (Test-Path "$c\EscapeFromTarkov_Data")) { return $c }
    }
    return $null
  }
  $eft = Find-Eft
  if (-not $eft) {
    Die "EFT not found in the registry or common paths. This script assumes Escape From Tarkov is already installed via the BSG Launcher - or pass -EftPath `"D:\path\to\EFT`"."
  }
  Say "EFT found: $eft" "Green"

  $consistency = Get-Content "$eft\ConsistencyInfo" -TotalCount 5 -ErrorAction SilentlyContinue | Out-String
  if ($consistency -match '"Version"\s*:\s*"([0-9.]+)\.(\d+)"') { $eftBuild = $Matches[2] }
  if ($eftBuild -eq "") { Die "Could not read the game version from $eft\ConsistencyInfo - is the EFT install complete and updated?" }
  Say "EFT build: $eftBuild (SPT 4.1.6 wants $targetBuild)"
} else {
  Say "update mode - skipping EFT detection/copy/patcher" "Yellow"
  if (-not (Test-Path "$InstallDir\EscapeFromTarkov.exe")) { Die "update mode but no existing install at $InstallDir." }
}

# ---------- free-space gate (fresh need = EFT size + 25 GB headroom for patcher staging, SPT and mods) ----------
if ($Update) {
  $need = 10GB
} else {
  Say "measuring EFT size..."
  $eftBytes = (Get-ChildItem $eft -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
  if (-not $eftBytes) { $eftBytes = 57GB }
  $need = [int64]$eftBytes + 25GB
}
if ($drive.Free -lt $need) {
  Die "Only $([int]($drive.Free/1GB)) GB free on $($drive.Name): - need $([int]($need/1GB)) GB $(if ($Update) {'(update mode)'} else {'(fresh install: EFT copy + patcher + SPT + mods)'})."
}

# ---------- 2. stage tools ----------
$cache = Join-Path $env:LOCALAPPDATA "spt-setup-cache"
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$zr = Join-Path $cache "7zr.exe"
if (-not (Test-Path $zr)) { curl.exe -sSL -o $zr $sevenZipR }

function Get-File($url, $dest) {
  if ((Test-Path $dest) -and ((Get-Item $dest).Length -gt 0)) { Say "cached: $(Split-Path -Leaf $dest)"; return }
  try {
    $len = (Invoke-WebRequest -Method Head -Uri $url -UseBasicParsing -TimeoutSec 10).Headers['Content-Length']
    Say "downloading $(Split-Path -Leaf $dest) ($([int]($len/1MB)) MB)"
  } catch {
    Say "downloading $(Split-Path -Leaf $dest)"
  }
  curl.exe -sSL --retry 3 --retry-delay 2 -o $dest $url
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dest)) { Die "download failed: $url" }
}

# ---------- patcher preflight (BEFORE any copying: fail early, fail clean) ----------
$patch = $null; $pzip = $null; $pstage = $null
if (-not $Update -and $eftBuild -ne $targetBuild) {
  Get-File "$($patchHosts[0])/mirrors.json" (Join-Path $cache "mirrors.json")
  $patches = (Get-Content (Join-Path $cache "mirrors.json") -Raw | ConvertFrom-Json).Patches
  $patch = $patches | Where-Object { $_.SourceClientVersion -eq [int]$eftBuild -and $_.TargetClientVersion -eq [int]$targetBuild } | Select-Object -First 1
  if (-not $patch) {
    Say "no direct patch from build $eftBuild to $targetBuild. Known sources: $($patches.SourceClientVersion -join ', ')" "Yellow"
    Die "Your EFT build has no published downgrade patch yet. Update-or-wait - ask Matt."
  }
  # patch archive + unpacked deltas live on the INSTALL drive (the LOCALAPPDATA cache on C: is often tiny)
  $pstage = Join-Path (Split-Path -Qualifier $InstallDir) "spt-patcher-stage"
  $pzip = Join-Path $pstage "patcher-$eftBuild-$targetBuild.7z"
  $phead = $null
  try { $phead = (Invoke-WebRequest -Method Head -Uri $patch.Mirrors[0].Link -UseBasicParsing -TimeoutSec 15).Headers['Content-Length'] } catch { }
  $patchBytes = if ($phead) { [int64]"$($phead | Select-Object -First 1)" } else { 10GB }
  $needBytes = $patchBytes * 3 + 2GB
  if ($drive.Free -lt $needBytes) {
    Die "not enough space for the downgrade patch on $($drive.Name): - need $([int]($needBytes/1GB)) GB free (3x patch size + margin), have $([int]($drive.Free/1GB)) GB. Free up space and re-run - nothing has been copied yet."
  }
  Say "patcher preflight ok ($([int]($patchBytes/1MB)) MB patch, staging on $($drive.Name):)" "Green"
}

# ---------- 3. copy EFT ----------
if ($Update) {
  Say "existing install at $InstallDir - refreshing Fika + mod layer only" "Yellow"
} else {
  if (Test-Path "$InstallDir\EscapeFromTarkov.exe") { Die "$InstallDir already contains game files. Use -Update to refresh mods only, or pick an empty folder." }
  Say "copying EFT -> $InstallDir (~57 GB, coffee time)..."
  New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
  Start-Ticker "copying EFT"
  robocopy $eft $InstallDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
  Stop-Ticker
  if (-not (Test-Path "$InstallDir\EscapeFromTarkov.exe")) { Die "EFT copy failed." }
  Say "EFT copy complete" "Green"
}

# ---------- 4. downgrade patcher (only when build mismatch) ----------
if (-not $Update -and $eftBuild -ne $targetBuild) {
  Say "EFT build $eftBuild != $targetBuild - downloading downgrade patcher..."
  Start-Ticker "downgrading the client"
  New-Item -ItemType Directory -Force -Path $pstage | Out-Null
  Get-File $patch.Mirrors[0].Link $pzip
  Say "extracting patcher..."
  & $zr x -y "-o$($pstage)\unpack" $pzip | Out-Null
  if ($LASTEXITCODE -gt 1) { Die "patcher archive extraction failed (disk full?). 7z exit $LASTEXITCODE." }
  # official flow: a Patcher* subdir (or archive root) holds patcher.exe + SPT_Patches
  $pdir = Get-ChildItem "$pstage\unpack" -Directory -Filter "Patcher*" | Select-Object -First 1
  if (-not $pdir) { $pdir = Get-Item "$pstage\unpack" }
  robocopy $pdir.FullName $InstallDir /E /MOVE /NFL /NDL /NJH /NJS /NP | Out-Null
  $patcherExe = Join-Path $InstallDir "patcher.exe"
  if (-not (Test-Path $patcherExe)) { Die "patcher.exe not found after extraction." }
  Say "running patcher.exe autoclose (exit 10 = success)..."
  $proc = Start-Process -FilePath $patcherExe -ArgumentList "autoclose" -WorkingDirectory $InstallDir -PassThru -Wait
  Stop-Ticker
  if ($proc.ExitCode -ne 10) { Die "patcher exited with code $($proc.ExitCode) (10 = success)." }
  Say "patch applied - cleaning up patcher files" "Green"
  Remove-Item $patcherExe -Force
  Remove-Item (Join-Path $InstallDir "SPT_Patches") -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item (Join-Path $InstallDir "patcher.log") -Force -ErrorAction SilentlyContinue
  Remove-Item $pstage -Recurse -Force -ErrorAction SilentlyContinue
} elseif (-not $Update) {
  Say "EFT build already matches $targetBuild - no patching needed" "Green"
}

# ---------- 5. SPT 4.1.6 layer ----------
if (-not (Test-Path "$InstallDir\SPT_Runtime\SPT.Server.exe")) {
  $szip = Join-Path $cache "SPT-4.1.6.7z"
  Start-Ticker "unpacking SPT 4.1.6"
  Get-File $spt7z $szip
  Say "extracting SPT runtime..."
  & $zr x -y "-o$InstallDir" $szip | Out-Null
  Stop-Ticker
}
if (-not (Test-Path "$InstallDir\SPT_Runtime\SPT.Server.exe")) { Die "SPT_Runtime missing after extraction." }
Say "SPT 4.1.6 layer in place" "Green"

# ---------- 6. Fika ----------
Get-File $fikaPlugin (Join-Path $cache "Fika.Plugin.zip")
Get-File $fikaServer (Join-Path $cache "Fika.Server.zip")
tar -xf (Join-Path $cache "Fika.Plugin.zip") -C $InstallDir
tar -xf (Join-Path $cache "Fika.Server.zip") -C $InstallDir
if (-not (Test-Path "$InstallDir\BepInEx\plugins\Fika\Fika.Core.dll")) { Die "Fika client plugin missing after extraction." }
Say "Fika 2.4.6 client + 2.4.2 server in place" "Green"

# ---------- 7. mods ----------
function Expand-Mod($zip, $destRoot) {
  # SPT zips are root-anchored (BepInEx/..., SPT_Runtime/...); a bare user/ tree goes under SPT_Runtime.
  # loader.json is never overwritten: it points SVM at the live preset and clobbering it disables SVM.
  $stage = Join-Path $cache "staging"
  if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
  New-Item -ItemType Directory -Force -Path $stage | Out-Null
  $sig = (Get-Content $zip -Encoding Byte -TotalCount 2 | ForEach-Object { [char]$_ }) -join ''
  if ($sig -eq '7z') { & $zr x -y "-o$stage" $zip | Out-Null } else { tar -xf $zip -C $stage }
  $entries = @(Get-ChildItem $stage)
  $base = $stage
  if (($entries.Count -eq 1) -and ($entries[0].PSIsContainer)) {
    $inner = @(Get-ChildItem $entries[0].FullName -Name)
    $hit = $false
    foreach ($i in $inner) { if ($i -in @('BepInEx','SPT_Runtime','user','EscapeFromTarkov_Data')) { $hit = $true } }
    if ($hit) { $base = $entries[0].FullName }
  }
  foreach ($t in @(Get-ChildItem $base -Name)) {
    $src = Join-Path $base $t
    if (Test-Path $src -PathType Container) {
      $dst = if ($t -eq 'user') { Join-Path $destRoot 'SPT_Runtime\user' } else { Join-Path $destRoot $t }
      # /XF keeps tuned files: loader.json (SVM->preset link), config.json (QuestExp/ABPS tuning),
      # Noname.json (live SVM preset), fika.jsonc (Fika tuning) - versions are pinned, zips only re-ship stock
      robocopy $src $dst /E /XF loader.json config.json fika.jsonc Noname.json /NFL /NDL /NJH /NJS /NP | Out-Null
    } else {
      Copy-Item $src (Join-Path $destRoot $t) -Force
    }
  }
}

$done = 0
Start-Ticker "installing mods"
foreach ($row in $ModManifest) {
  $parts = $row.Split('|')
  $modId = $parts[0]; $kind = $parts[1]; $slug = $parts[2]; $ver = $parts[3]
  $zip = Join-Path $cache "$slug-$ver.zip"
  Get-File "https://sp-mod.com/$kind/download/$modId/$slug/$ver" $zip
  Expand-Mod $zip $InstallDir
  $done++
  Say "  [$done/$($ModManifest.Count)] $slug $ver"
}
Stop-Ticker
Say "all $done mods installed" "Green"

# ---------- 8. Fika Force IP (advertise wireguard addr when hosting raids) ----------
$fikaCfg = Join-Path $InstallDir "BepInEx\config\com.fika.core.cfg"
$wgIp = ""
$wgs = @(Get-NetAdapter | Where-Object { ($_.InterfaceDescription -match 'WireGuard') -or ($_.Name -match 'wg|wireguard|bb-games') })
foreach ($a in $wgs) {
  $ipa = Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
  if ($ipa) { $wgIp = @($ipa)[0].IPAddress; break }
}
if ($wgIp -ne "") {
  if (Test-Path $fikaCfg) {
    $txt = [System.IO.File]::ReadAllText($fikaCfg)
    if ($txt -match '(?m)^Force IP = ') {
      $txt = [regex]::Replace($txt, '(?m)^Force IP = .*$', "Force IP = $wgIp")
    } elseif ($txt -match '(?m)^\[Network\]') {
      $txt = [regex]::Replace($txt, '(?m)^(\[Network\])', "`$1`r`nForce IP = $wgIp")
    } else {
      $txt = $txt.TrimEnd() + "`r`n[Network]`r`nForce IP = $wgIp`r`n"
    }
  } else {
    New-Item -ItemType Directory -Force -Path (Split-Path $fikaCfg) | Out-Null
    $txt = "[Network]`r`nForce IP = $wgIp`r`n"
  }
  [System.IO.File]::WriteAllText($fikaCfg, $txt)
  Say "Fika Force IP = $wgIp (hosted raids advertise the wireguard addr)" "Green"
} else {
  Say "No WireGuard adapter found - Force IP left empty (Fika auto-detects, may hand peers a LAN IP)" "Yellow"
}

# ---------- 9. launcher -> remote server ----------
$ls = Join-Path $InstallDir "SPT_Runtime\user\Launcher\LauncherSettings.json"
New-Item -ItemType Directory -Force -Path (Split-Path $ls) | Out-Null   # fresh installs have no user\Launcher yet
if (Test-Path $ls) {
  Copy-Item $ls "$ls.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
  $cfg = Get-Content $ls -Raw | ConvertFrom-Json
} else {
  $cfg = [pscustomobject]@{ Servers = @() }
}
$entry = [pscustomobject]@{ Name = "Matt Co-op"; IpAddress = $ServerHost; ServerId = "$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())" }
$cfg.Servers = @($entry)
[System.IO.File]::WriteAllText($ls, ($cfg | ConvertTo-Json -Depth 6))
Say "launcher pointed at https://$ServerHost" "Green"

# ---------- 10. firewall ----------
New-NetFirewallRule -DisplayName "SPT Client - EscapeFromTarkov" -Direction Inbound -Program "$InstallDir\EscapeFromTarkov.exe" -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName "SPT Client - SPT.Server" -Direction Inbound -Program "$InstallDir\SPT_Runtime\SPT.Server.exe" -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName "Fika P2P - UDP 25565" -Direction Inbound -Protocol UDP -LocalPort 25565 -Action Allow -Profile Any | Out-Null
Say "firewall rules added (game, server, Fika P2P)" "Green"

# ---------- 11. verify ----------
$checks = @(
  ("SPT server binary",     "$InstallDir\SPT_Runtime\SPT.Server.exe"),
  ("SPT launcher",          "$InstallDir\SPT_Runtime\SPT.Launcher.exe"),
  ("Fika client plugin",    "$InstallDir\BepInEx\plugins\Fika\Fika.Core.dll"),
  ("Fika server mod",       "$InstallDir\SPT_Runtime\user\mods\fika-server"),
  ("SVM settings UI",       "$InstallDir\Greed.exe"),
  ("WTT Content Backport",  "$InstallDir\SPT_Runtime\user\mods\WTT-ContentBackport"),
  ("WTT CommonLib",         "$InstallDir\SPT_Runtime\user\mods\WTT-ServerCommonLib")
)
$fail = $false
foreach ($c in $checks) {
  $ok = Test-Path $c[1]
  Say ("  {0,-24} {1}" -f $c[0], $(if ($ok) {"OK"} else {"MISSING"})) $(if ($ok) {"Green"} else {"Red"})
  if (-not $ok) { $fail = $true }
}
$ping = curl.exe -sk -o NUL -w "%{http_code}" --connect-timeout 8 "https://$ServerHost/"
Say "  server reachability    HTTP $ping" $(if ($ping -eq "200") {"Green"} else {"Yellow"})

Say ""
if ($fail) { Die "some files are missing - see above." }
Say "ALL DONE. Open SPT_Runtime\SPT.Launcher.exe (NOT EscapeFromTarkov.exe!), pick 'Matt Co-op', and yell in Discord when you're in." "Green"
Say "Desktop icon? Right-click SPT.Launcher.exe -> Show more options -> Send to -> Desktop (create shortcut)." "Green"
