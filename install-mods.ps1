<#
  SPT co-op MOD LAYER for an EXISTING SPT install.
  Drops Fika 2.4.x + the 54 pinned mods + co-op config into the SPT folder it lives in.
  No arguments: put this file (and run-mods.bat) INSIDE the SPT folder (the one with
  SPT_Runtime\ in it) and double-click run-mods.bat. Safe to re-run: refreshes the mod
  layer only, never deletes, never touches tuned configs (loader.json / config.json /
  fika.jsonc / Noname.json), quarantines nothing of yours. Never patches, never copies EFT.

  Manual run:  powershell -ExecutionPolicy Bypass -File install-mods.ps1 [-SptDir D:\SPT]
#>
param(
  [string]$SptDir = "",
  [string]$ServerHost = "10.4.20.2:6969"
)
$ErrorActionPreference = "Continue"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$fikaPlugin = "https://github.com/project-fika/Fika-Plugin/releases/download/v2.4.6/Fika.Release.2.4.6.zip"
$fikaServer = "https://github.com/project-fika/Fika-Server-CSharp/releases/download/v2.4.2/Fika.Server.Release.2.4.2.zip"
$sevenZipR  = "https://www.7-zip.org/a/7zr.exe"

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
function Die($m) { Write-Host "MOD-LAYER FAILED: $m" -ForegroundColor Red; exit 1 }

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
      [Console]::WriteLine(("  {0} [{1}] {2}  --  {3}" -f $spin[$i % 4], $t, $label, $puns[$i % $puns.Count]))
      $i++
    }
  }).AddArgument($puns).AddArgument($label).AddArgument($every)
  [void]$script:tickPs.BeginInvoke()
}
function Stop-Ticker {
  if ($script:tickPs) { $script:tickPs.Stop(); $script:tickPs.Dispose(); $script:tickPs = $null }
}

# ---------- 0. preflight (firewall rules want admin) ----------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole("Administrators")) {
  Say "needs an admin shell - asking Windows to elevate (click Yes on the UAC prompt)..."
  $relaunchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
  if ($SptDir -ne "") { $relaunchArgs += @('-SptDir', ('"{0}"' -f $SptDir)) }
  try {
    Start-Process -FilePath "powershell.exe" -Verb RunAs -Wait -ArgumentList $relaunchArgs | Out-Null
    exit 0
  } catch {
    Die "elevation declined. Re-run from an ADMIN PowerShell: press Win+X -> Windows PowerShell (Admin), then: powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`""
  }
}

# target folder: -SptDir, else the folder this script lives in, else the current directory
if ($SptDir -eq "") { $SptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path } }
$SptDir = $SptDir.TrimEnd('\')
if (-not (Test-Path "$SptDir\SPT_Runtime\SPT.Server.exe")) {
  Die "$SptDir does not look like an SPT folder (no SPT_Runtime\SPT.Server.exe). Put this script INSIDE the SPT folder and double-click run-mods.bat."
}
Say "SPT folder: $SptDir" "Green"

$cache = Join-Path $env:LOCALAPPDATA "spt-setup-cache"
New-Item -ItemType Directory -Force -Path $cache | Out-Null

function Get-File($url, $dest) {
  if ((Test-Path $dest) -and ((Get-Item $dest).Length -gt 0)) { Say "cached: $(Split-Path -Leaf $dest)"; return }
  # local-first: any archive dropped NEXT TO THIS SCRIPT wins over the network.
  $want = [System.IO.Path]::GetFileNameWithoutExtension($dest)
  $wn = ($want -replace '[^A-Za-z0-9]', '').ToLower()
  $local = $null
  if ($PSScriptRoot -and ($wn.Length -gt 0)) {
    $ext = [System.IO.Path]::GetExtension($dest)
    if ($null -eq $script:localFiles) {
      # walk ONCE: this script often lives inside a huge game folder
      $script:localFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -Depth 2 -File -ErrorAction SilentlyContinue)
    }
    $seen = @()
    $exact = @()
    $fuzzy = @()
    $walpha = ($want -replace '[^a-zA-Z]', '').ToLower()
    if ($walpha.Length -gt 3) { $walpha = $walpha.Substring(0, 3) }
    $wver = ""
    $mv = [regex]::Match($want, '\d+\.\d+\.\d+')
    if ($mv.Success) { $wver = ($mv.Value -replace '[^0-9]', '') }
    foreach ($f in $script:localFiles) {
      if (@(".7z", ".zip") -contains $f.Extension.ToLower()) { $seen += $f.Name }
      if ($f.Extension -ine $ext) { continue }
      $cn = ($f.BaseName -replace '[^A-Za-z0-9]', '').ToLower()
      if ($cn.Length -eq 0) { continue }
      if ($cn -eq $wn) { $exact += $f }
      elseif ($cn.StartsWith($wn)) { $fuzzy += $f }
      elseif (($cn.Length -ge 6) -and ($wn.StartsWith($cn))) { $fuzzy += $f }
      elseif (($wver.Length -ge 3) -and ($cn.Contains($wver)) -and ($walpha.Length -eq 0 -or $cn.Contains($walpha))) { $fuzzy += $f }
    }
    if ($exact.Count -gt 0) { $local = @($exact | Sort-Object { $_.FullName.Length })[0] }
    elseif ($fuzzy.Count -gt 0) { $local = @($fuzzy | Sort-Object { ($_.BaseName -replace '[^A-Za-z0-9]', '').Length } -Descending)[0] }
    if (($null -eq $local) -and ($seen.Count -gt 0) -and (@(".7z", ".zip") -contains $ext.ToLower())) {
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
  curl.exe -sSL --retry 3 --retry-delay 2 -o $dest $url
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dest)) { Die "download failed: $url" }
}

# ---------- 1. tools ----------
$zr = Join-Path $cache "7zr.exe"
Get-File $sevenZipR $zr
if (-not (Test-Path $zr)) { Die "7zr.exe unavailable - drop 7zr.exe next to this script and re-run." }

# ---------- 2. Fika ----------
Get-File $fikaPlugin (Join-Path $cache "Fika.Plugin.zip")
Get-File $fikaServer (Join-Path $cache "Fika.Server.zip")
tar -xf (Join-Path $cache "Fika.Plugin.zip") -C $SptDir
tar -xf (Join-Path $cache "Fika.Server.zip") -C $SptDir
if (-not (Test-Path "$SptDir\BepInEx\plugins\Fika\Fika.Core.dll")) { Die "Fika client plugin missing after extraction." }
Say "Fika 2.4.6 client + 2.4.2 server in place" "Green"

# ---------- 3. mods ----------
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
  Expand-Mod $zip $SptDir
  $done++
  Say "  [$done/$($ModManifest.Count)] $slug $ver"
}
Stop-Ticker
Say "all $done mods installed" "Green"

# ---------- 4. Fika Force IP (advertise wireguard addr when hosting raids) ----------
$fikaCfg = Join-Path $SptDir "BepInEx\config\com.fika.core.cfg"
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

# ---------- 5. launcher -> remote server ----------
$ls = Join-Path $SptDir "SPT_Runtime\user\Launcher\LauncherSettings.json"
New-Item -ItemType Directory -Force -Path (Split-Path $ls) | Out-Null
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

# ---------- 6. firewall ----------
New-NetFirewallRule -DisplayName "SPT Client - EscapeFromTarkov" -Direction Inbound -Program "$SptDir\EscapeFromTarkov.exe" -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName "SPT Client - SPT.Server" -Direction Inbound -Program "$SptDir\SPT_Runtime\SPT.Server.exe" -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName "Fika P2P - UDP 25565" -Direction Inbound -Protocol UDP -LocalPort 25565 -Action Allow -Profile Any | Out-Null
Say "firewall rules added (game, server, Fika P2P)" "Green"

# ---------- 7. verify ----------
$checks = @(
  ("SPT server binary",     "$SptDir\SPT_Runtime\SPT.Server.exe"),
  ("SPT launcher",          "$SptDir\SPT_Runtime\SPT.Launcher.exe"),
  ("Fika client plugin",    "$SptDir\BepInEx\plugins\Fika\Fika.Core.dll"),
  ("Fika server mod",       "$SptDir\SPT_Runtime\user\mods\fika-server"),
  ("SVM settings UI",       "$SptDir\Greed.exe"),
  ("WTT Content Backport",  "$SptDir\SPT_Runtime\user\mods\WTT-ContentBackport"),
  ("WTT CommonLib",         "$SptDir\SPT_Runtime\user\mods\WTT-ServerCommonLib")
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
