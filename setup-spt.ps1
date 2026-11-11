#Requires -Version 5.1
<#
.SYNOPSIS
  SPT + FIKA + modpack one-shot installer for Matt's co-op crew (Windows 10/11 x64).
.DESCRIPTION
  Replicates the official SPT installer non-interactively:
    EFT copy -> optional downgrade patcher -> SPT 4.1.6 layer -> Fika 2.4.x -> 52 pinned mods
    -> launcher pointed at the co-op server -> firewall rules -> verify.

  Assumes Escape From Tarkov IS installed (BSG Launcher, you must own the game). The script
  locates it via registry + common paths (override with -EftPath) and stops with a clear
  message if it cannot find it - it does not install or repair EFT itself.

  -Update upgrades an EXISTING install in place. It reads the client build from
  <InstallDir>\ConsistencyInfo and, when that differs from the pinned SPT target build, runs the
  SAME downgrade patcher as a fresh install (mirrors.json -> patch archive -> staged on the
  install drive -> robocopy /MOVE -> patcher.exe autoclose). It then adds the SPT layer only if
  SPT_Runtime\SPT.Server.exe is missing, and refreshes Fika + the mod layer from the PINNED
  manifest (version bumps arrive with a re-pinned manifest - everyone staying identical is the
  point). It never copies EFT and never deletes anything from your user tree.

  The pin list comes from -ManifestPath (JSON: {"mods":["id|kind|slug|version", ...]}), else
  mods.json next to this script, else the built-in $ModManifest block below.

  -ArchiveDir adds a local archive folder, searched recursively. Upstream URLs still win; the
  local copy is only the offline fallback. The portable app passes <exeDir>\archives.

  -NoElevate never re-launches itself elevated - it fails fast instead. Use it when the caller
  is already elevated and needs this process' console output (the app host does).

  Run with no arguments and the script asks where SPT should live (Enter accepts
  C:\SPT). Pass -InstallDir to skip the prompt (automation / -Update runs).

  Prereqs (checked up front):
    - Escape From Tarkov installed via BSG Launcher [not needed for -Update]
    - .NET 10 Desktop + ASP.NET Core 10 runtimes
    - EFT size + ~25 GB free for a fresh install (~85 GB for a standard EFT) / ~10 GB for -Update
      (~10 GB more when -Update has to stage a game patch: 3x patch size + margin)
    - Admin PowerShell (or -NoElevate from an already elevated host)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File setup-spt.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File setup-spt.ps1 -InstallDir D:\SPT -ServerHost 10.4.20.2:6969
#>
param(
  [string]$InstallDir = "C:\SPT",
  [string]$ServerHost = "10.4.20.2:6969",
  [string]$EftPath = "",
  [string]$ArchiveDir = "",
  [string]$ManifestPath = "",
  [switch]$NoElevate,
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
# >>> GENERATED-MANIFEST (manifest/refresh.ps1 rewrites this block; do not hand-edit)
$ModManifest = @(
  "2512|mod|wtt-content-backport|2.0.5",
  "2310|mod|wtt-commonlib|3.0.6",
  "1342|mod|ui-fixes|6.0.4",
  "1358|mod|brightlasers|3.0.0",
  "1884|mod|continuous-healing|1.6.3",
  "236|mod|server-value-modifier-svm|2.2.3",
  "2025|mod|all-quests-checkmarks|1.4.0",
  "2097|mod|abps-acids-bot-placement-system|2.1.0",
  "1621|mod|shadow-flicker-fix|1.5.3",
  "2314|mod|season-rotator|2.0.1",
  "2112|mod|continuous-load-ammo|1.2.0",
  "2386|mod|use-items-anywhere|2.1.6",
  "1657|mod|mergeconsumables|1.6.3",
  "2003|mod|hollywoodfx|3.0.0",
  "167|mod|ammo-stats|4.1.0",
  "1343|mod|skipper|1.1.5",
  "2790|mod|easy-mounting|2.0.1",
  "2734|mod|better-night-skies|2.0.0",
  "1431|mod|dynamic-maps|1.2.1",
  "2540|mod|content-backport-prestiges|1.1.0",
  "2689|mod|item-property-backport|1.5.2",
  "2866|mod|manimals-icebreaker-backport|1.2.4",
  "98|addon|manimals-icebreaker-backport-fika-sync|1.2.4",
  "3179|mod|interface-11-backport|1.0.4",
  "3211|mod|server-room-backport|1.0.0",
  "3040|mod|map-variants|1.1.0",
  "3193|mod|tushonka-mapping-sdk|1.9.3",
  "2714|mod|discord-raid-map|2.0.0",
  "2049|mod|add-secret-exfil-notes-to-caches|2.1.0",
  "1888|mod|better-keys-ng|1.8.0",
  "3047|mod|deploy-screen-overhaul|1.1.0",
  "2426|mod|morebotsapi|2.1.2",
  "2511|mod|wtt-black-division-redacted-home|1.3.1",
  "2649|mod|climbable-ladders|1.0.4",
  "2864|mod|manimals-cs-gas|2.0.1",
  "3025|mod|manimals-10-interchange-expansion-backport|1.0.8",
  "1426|mod|unitytoolkit|2.0.2",
  "791|mod|sain-solarints-ai-modifications-full-ai-combat-system-replacement|4.5.1",
  "827|mod|waypoints-expanded-navmesh|1.9.0",
  "3236|mod|return-of-thunderstorms|1.2.4",
  "2153|mod|expanded-task-text-ett|2.1.0",
  "2681|mod|manimals-ammo-loading-animations|2.0.0",
  "1374|mod|player-encumbrance-bar|1.3.0",
  "3214|mod|quest-glow|1.0.0",
  "2879|mod|weboverlay|1.12.0",
  "865|mod|gilded-key-storage|2.1.1",
  "1860|mod|classic-movement|1.1.3",
  "2766|mod|quest-experience-multiplier|1.0.1",
  "2892|mod|remove-single-raid-quest-requirements|1.1.0",
  "2966|mod|presettrader|1.2.0",
  "2407|mod|quicksellflea|1.4.5",
  "3242|mod|zezinhos-shopping-list|1.2.4"
)
# <<< GENERATED-MANIFEST

function Say($m, $c = "White") { Write-Host ">> $m" -ForegroundColor $c }
function Die($m) { Write-Host "SETUP FAILED: $m" -ForegroundColor Red; exit 1 }

# ---------- manifest resolution: -ManifestPath -> mods.json next to the script -> built-in pins ----------
function Read-ManifestPins($path) {
  if (-not (Test-Path -LiteralPath $path)) { Die "manifest not found: $path" }
  $json = $null
  try { $json = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json }
  catch { Die "manifest unreadable: $path ($($_.Exception.Message))" }
  $rows = @($json.mods | Where-Object { $null -ne $_ })
  if ($rows.Count -eq 0) { Die "manifest invalid: $path has no mods[] rows." }
  $n = 0
  foreach ($r in $rows) {
    $n++
    $row = "$r".Trim()
    $fields = @($row -split '\|')
    $bad = ($fields.Count -ne 4)
    if (-not $bad) { foreach ($f in $fields) { if ("$f".Trim() -eq "") { $bad = $true } } }
    if ($bad) { Die "manifest invalid at row $n`: $row" }
  }
  # plain return: the caller wraps with @() and gets one element per pin (",$rows" would hand
  # back a single nested array, so a 54-pin manifest would look like 1 pin to the loop)
  return $rows
}

$manifestSource = ""
if ($ManifestPath -ne "") {
  $ModManifest = @(Read-ManifestPins $ManifestPath)
  $manifestSource = $ManifestPath
} elseif ($PSScriptRoot -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot "mods.json"))) {
  $scriptModsJson = Join-Path $PSScriptRoot "mods.json"
  $ModManifest = @(Read-ManifestPins $scriptModsJson)
  $manifestSource = $scriptModsJson
} else {
  $manifestSource = "built-in pins in setup-spt.ps1"
}
Say "using manifest: $($ModManifest.Count) pins from $manifestSource" "Green"

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
$isAdmin = $false
try {
  $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
  $isAdmin = $principal.IsInRole("Administrators")
} catch {
  $isAdmin = $false
}
if (-not $isAdmin) {
  if ($NoElevate) {
    # the app host is already elevated and captures THIS process' console - a RunAs relaunch would
    # open a second window, leave the host with no output and hang its progress parser.
    Die "needs administrator rights - the app runs this elevated; run it from an admin PowerShell instead."
  }
  Say "needs an admin shell - asking Windows to elevate (click Yes on the UAC prompt)..."
  $relaunchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
  if ($PSBoundParameters.ContainsKey('InstallDir')) { $relaunchArgs += @('-InstallDir', ('"{0}"' -f $InstallDir)) }
  if ($ServerHost -ne "10.4.20.2:6969") { $relaunchArgs += @('-ServerHost', $ServerHost) }
  if ($EftPath -ne "") { $relaunchArgs += @('-EftPath', ('"{0}"' -f $EftPath)) }
  if ($ArchiveDir -ne "") { $relaunchArgs += @('-ArchiveDir', ('"{0}"' -f $ArchiveDir)) }
  if ($ManifestPath -ne "") { $relaunchArgs += @('-ManifestPath', ('"{0}"' -f $ManifestPath)) }
  if ($Update) { $relaunchArgs += '-Update' }
  try {
    Start-Process -FilePath "powershell.exe" -Verb RunAs -Wait -ArgumentList $relaunchArgs | Out-Null
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
  Say "update mode - reusing the existing install at $InstallDir (never copies EFT)" "Yellow"
  if (-not (Test-Path "$InstallDir\EscapeFromTarkov.exe")) { Die "update mode but no existing install at $InstallDir - pass -InstallDir with the folder that holds EscapeFromTarkov.exe." }
  $consistency = Get-Content "$InstallDir\ConsistencyInfo" -TotalCount 5 -ErrorAction SilentlyContinue | Out-String
  if ($consistency -match '"Version"\s*:\s*"([0-9.]+)\.(\d+)"') { $eftBuild = $Matches[2] }
  if ($eftBuild -eq "") {
    Die "could not read the installed game build from $InstallDir\ConsistencyInfo - that file is missing or unreadable, so update mode cannot tell whether the downgrade patch is needed. Point -InstallDir at the SPT folder (the one with EscapeFromTarkov.exe)."
  }
  Say "installed build: $eftBuild (SPT 4.1.6 wants $targetBuild)"
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

Say "build 2026-11-06-r3 - local-detect (recursive)"

# ---------- 2. stage tools ----------
$cache = Join-Path $env:LOCALAPPDATA "spt-setup-cache"
New-Item -ItemType Directory -Force -Path $cache | Out-Null

function Get-File($url, $dest) {
  if ((Test-Path $dest) -and ((Get-Item $dest).Length -gt 0)) { Say "cached: $(Split-Path -Leaf $dest)"; return }
  # local-first: -ArchiveDir (searched recursively) wins over the network, then any archive
  # dropped NEXT TO THIS SCRIPT. exact-name match, else prefix match
  # (e.g. 'SPT-4.1.6-40743-731d7a2.7z' serves 'SPT-4.1.6.7z'), else version-token fuzzy.
  $want = [System.IO.Path]::GetFileNameWithoutExtension($dest)
  $wn = ($want -replace '[^A-Za-z0-9]', '').ToLower()
  $local = $null
  $localDirs = @()
  if (($PSScriptRoot -or $ArchiveDir) -and ($wn.Length -gt 0)) {
    $ext = [System.IO.Path]::GetExtension($dest)
    $seen = @()
    $walpha = ($want -replace '[^a-zA-Z]', '').ToLower()
    if ($walpha.Length -gt 3) { $walpha = $walpha.Substring(0, 3) }
    $wver = ""
    $mv = [regex]::Match($want, '\d+\.\d+\.\d+')
    if ($mv.Success) { $wver = ($mv.Value -replace '[^0-9]', '') }
    # search order: the app's -ArchiveDir first, then next to the script AND nearby subfolders
    # (offline\, spt-setup-cache\, ...). First directory that yields a match wins.
    $searchDirs = @()
    if ($ArchiveDir) { $searchDirs += @{ Path = $ArchiveDir; Depth = 32 }; $localDirs += $ArchiveDir }
    if ($PSScriptRoot) { $searchDirs += @{ Path = $PSScriptRoot; Depth = 2 }; $localDirs += $PSScriptRoot }
    foreach ($sd in $searchDirs) {
      if (-not (Test-Path -LiteralPath $sd.Path)) { continue }
      $exact = @()
      $fuzzy = @()
      foreach ($f in (Get-ChildItem -LiteralPath $sd.Path -Recurse -Depth $sd.Depth -File -ErrorAction SilentlyContinue)) {
        if (@(".7z", ".zip") -contains $f.Extension.ToLower()) { $seen += $f.Name }
        if ($f.Extension -ine $ext) { continue }
        $cn = ($f.BaseName -replace '[^A-Za-z0-9]', '').ToLower()
        if ($cn.Length -eq 0) { continue }
        if ($cn -eq $wn) { $exact += $f }
        elseif ($cn.StartsWith($wn)) { $fuzzy += $f }
        elseif (($cn.Length -ge 6) -and ($wn.StartsWith($cn))) { $fuzzy += $f }
        elseif (($wver.Length -ge 3) -and ($cn.Contains($wver)) -and ($walpha.Length -eq 0 -or $cn.Contains($walpha))) { $fuzzy += $f }
      }
      if ($exact.Count -gt 0) { $local = @($exact | Sort-Object { $_.FullName.Length })[0]; break }
      if ($fuzzy.Count -gt 0) { $local = @($fuzzy | Sort-Object { ($_.BaseName -replace '[^A-Za-z0-9]', '').Length } -Descending)[0]; break }
    }
    if (($null -eq $local) -and ($seen.Count -gt 0)) {
      Say ("no local match for " + (Split-Path -Leaf $dest) + " - archives found: " + ((@($seen) | Select-Object -Unique | Select-Object -First 8) -join ", ")) "Yellow"
    }
  }
  if ($null -ne $local) {
    Say "using local copy: $($local.Name)" "Green"
    Copy-Item -LiteralPath $local.FullName -Destination $dest -Force
    return
  }
  try {
    $len = (Invoke-WebRequest -Method Head -Uri $url -UseBasicParsing -TimeoutSec 10).Headers['Content-Length']
    Say "downloading $(Split-Path -Leaf $dest) ($([int]($len/1MB)) MB)"
  } catch {
    Say "downloading $(Split-Path -Leaf $dest)"
  }
  # stage to .part and only promote on a clean exit: a killed curl must never leave a
  # truncated file in the cache that the next run accepts as "cached:".
  $part = "$dest.part"
  Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
  curl.exe -sSL --retry 3 --retry-delay 2 -o $part $url
  $got = ($LASTEXITCODE -eq 0) -and (Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part).Length -gt 0)
  if (-not $got) {
    Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
    $where = if ($localDirs.Count -gt 0) { $localDirs -join ', ' } else { "no local archive dir (-ArchiveDir not set)" }
    Die "download failed: $(Split-Path -Leaf $dest) - none matched in $where, and the upstream copy at $url did not download. Put the archive in one of those folders or check the network."
  }
  Move-Item -LiteralPath $part -Destination $dest -Force
}

$zr = Join-Path $cache "7zr.exe"
Get-File $sevenZipR $zr
if (-not (Test-Path $zr)) { Die "7zr.exe unavailable - drop 7zr.exe in -ArchiveDir or next to this script and re-run." }

# ---------- patcher preflight (BEFORE any copying: fail early, fail clean) ----------
$patch = $null; $pzip = $null; $pstage = $null
if ($eftBuild -ne $targetBuild) {
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
    $mode = if ($Update) { " in update mode" } else { "" }
    Die "not enough space for the downgrade patch$mode on $($drive.Name): - need $([int]($needBytes/1GB)) GB free (3x patch size + margin), have $([int]($drive.Free/1GB)) GB. Free up space and re-run - nothing has been copied yet."
  }
  Say "patcher preflight ok ($([int]($patchBytes/1MB)) MB patch, staging on $($drive.Name):)$(if ($Update) {' - update mode'})" "Green"
}

# ---------- 3. copy EFT ----------
if ($Update) {
  Say "existing install at $InstallDir - in-place update (game patch only on a build mismatch, then SPT/Fika/mods)" "Yellow"
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

# ---------- 4. downgrade patcher (only when build mismatch, fresh install OR update) ----------
if ($eftBuild -ne $targetBuild) {
  Say "EFT build $eftBuild != $targetBuild - downloading downgrade patcher..."
  Start-Ticker "downgrading the client"
  # a killed previous run can leave GBs of unpacked deltas on the install drive - start clean
  if ($pstage -and (Test-Path $pstage)) { Remove-Item $pstage -Recurse -Force -ErrorAction SilentlyContinue }
  try {
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
  } finally {
    # success AND every Die path: a failed/cancelled patch must never leave GBs of staged
    # deltas on the install drive (the game tree itself is only touched by patcher.exe)
    Stop-Ticker
    if ($pstage -and (Test-Path $pstage)) { Remove-Item $pstage -Recurse -Force -ErrorAction SilentlyContinue }
  }
} else {
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
