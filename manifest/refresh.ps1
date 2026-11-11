#!/usr/bin/env pwsh
<#
  manifest/refresh.ps1 — regenerate the installer's mod pin list from the Forge API.

  Runs on PowerShell 7 on Linux or Windows (no Windows-only cmdlets, no elevation).

  Modes
    (default)          dry run: re-query Forge for every pinned row, report version drift.
    -Write             rewrite manifest/mods.json with the reconciled pins.
    -Apply             -Write, then rewrite the GENERATED-MANIFEST pin block in setup-spt.ps1
                       and the $ModManifest block in install-mods.ps1 (adding the markers to
                       install-mods.ps1 if they are not there yet). Idempotent: a second
                       -Apply run produces no diff.
    -LiveSet <path>    resolve a live-set JSON (manifest/live-set.json) through its "mapping"
                       and print the add/remove diff against manifest/mods.json.

  Version rule (do not change): a mod version is eligible when its spt_version_constraint
  accepts the SPT target (4.1.6). Among eligible versions the HIGHEST semantic version wins.
  Never pick by published_at — newest-published is routinely an older-track build.
  Forge "addon" rows carry no SPT constraint (they use mod_version_constraint), so an addon
  is pinned to its highest semantic version and flagged "unconstrained-addon".

  Examples
    /tmp/opencode/pwsh/pwsh -NoProfile -File manifest/refresh.ps1
    /tmp/opencode/pwsh/pwsh -NoProfile -File manifest/refresh.ps1 -Write
    /tmp/opencode/pwsh/pwsh -NoProfile -File manifest/refresh.ps1 -Apply
    /tmp/opencode/pwsh/pwsh -NoProfile -File manifest/refresh.ps1 -LiveSet manifest/live-set.json
#>
[CmdletBinding()]
param(
  [switch]$Write,
  [switch]$Apply,
  [string]$LiveSet = "",
  [string]$ManifestPath = "",
  [string]$SptTarget = "",
  [string]$RepoRoot = "",
  [string]$ApiBase = "https://sp-mod.com/api/v0",
  [string]$CacheDir = "",
  [switch]$NoCache
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Off

# ---------------------------------------------------------------- paths / output helpers
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = (Split-Path -Parent $MyInvocation.MyCommand.Path) }
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $ScriptDir }
if (-not $ManifestPath) { $ManifestPath = Join-Path $ScriptDir "mods.json" }
if (-not $CacheDir) { $CacheDir = Join-Path $ScriptDir ".cache" }
$SetupScript = Join-Path $RepoRoot "setup-spt.ps1"
$ModsScript = Join-Path $RepoRoot "install-mods.ps1"

$MarkerStart = '# >>> GENERATED-MANIFEST (manifest/refresh.ps1 rewrites this block; do not hand-edit)'
$MarkerEnd = '# <<< GENERATED-MANIFEST'

function Say($msg, $color = "White") { Write-Host $msg -ForegroundColor $color }
function Fail($msg) {
  Write-Host "REFRESH FAILED: $msg" -ForegroundColor Red
  exit 1
}

# ---------------------------------------------------------------- semver
function Get-SemVer([string]$s) {
  if (-not $s) { return $null }
  $t = $s.Trim()
  if ($t -match '^[vV]') { $t = $t.Substring(1) }
  $t = $t -replace '\+.*$', ''
  $t = ($t -split '-')[0]
  $parts = $t -split '\.'
  $nums = @(0, 0, 0)
  for ($i = 0; $i -lt 3; $i++) {
    if ($i -lt $parts.Count -and $parts[$i] -match '^\d+') { $nums[$i] = [int]$matches[0] }
  }
  return $nums
}

function Get-VersionBase([string]$s) {
  # returns @{ Nums = @(major,minor,patch); Count = how many numeric parts were written }
  if (-not $s) { return $null }
  $t = $s.Trim()
  if ($t -match '^[vV]') { $t = $t.Substring(1) }
  $nums = New-Object System.Collections.Generic.List[int]
  foreach ($p in ($t -split '\.')) {
    if ($p -match '^\d+') { $nums.Add([int]$matches[0]) } else { break }
  }
  if ($nums.Count -eq 0) { return $null }
  $base = @(0, 0, 0)
  for ($i = 0; $i -lt [Math]::Min(3, $nums.Count); $i++) { $base[$i] = $nums[$i] }
  return @{ Nums = $base; Count = $nums.Count }
}

function Compare-SemVer($a, $b) {
  for ($i = 0; $i -lt 3; $i++) {
    if ($a[$i] -ne $b[$i]) { return [Math]::Sign($a[$i] - $b[$i]) }
  }
  return 0
}

function Test-SptConstraint([string]$constraint, $target) {
  if (-not $constraint) { return $false }
  $c = $constraint.Trim()
  if (-not $c) { return $false }
  foreach ($group in ($c -split '\s*\|\|\s*')) {
    if (Test-ConstraintGroup $group.Trim() $target) { return $true }
  }
  return $false
}

function Test-ConstraintGroup([string]$group, $target) {
  if ($group -in @('*', 'x', 'X', 'any', 'Any')) { return $true }
  $tokens = @($group -split '[,\s]+' | Where-Object { $_ -ne '' })
  if ($tokens.Count -eq 0) { return $true }
  foreach ($t in $tokens) {
    if (-not (Test-ConstraintToken $t $target)) { return $false }
  }
  return $true
}

function Test-ConstraintToken([string]$token, $target) {
  if ($token -notmatch '^(>=|<=|>|<|==|=|\^|~)?\s*(.+)$') { return $false }
  $op = $matches[1]
  $rhs = $matches[2].Trim()
  if ($rhs -in @('*', 'x', 'X')) { return $true }
  $vb = Get-VersionBase $rhs
  if (-not $vb) { return $false }
  $base = $vb.Nums
  $count = $vb.Count

  $major = $base[0]
  $minor = $base[1]
  $patch = $base[2]

  if ($op -eq '~') {
    # ~1.2.3 -> >=1.2.3 <1.3.0 ; ~1.2 -> >=1.2.0 <1.3.0 ; ~1 -> >=1.0.0 <2.0.0
    if ($count -le 1) {
      $upper = @(($major + 1), 0, 0)
    }
    else {
      $upper = @($major, ($minor + 1), 0)
    }
    return ((Compare-SemVer $target $base) -ge 0) -and ((Compare-SemVer $target $upper) -lt 0)
  }
  if ($op -eq '^') {
    if ($major -gt 0 -or $count -le 1) {
      $upper = @(($major + 1), 0, 0)
    }
    elseif ($minor -gt 0 -or $count -eq 2) {
      $upper = @(0, ($minor + 1), 0)
    }
    else {
      $upper = @(0, 0, ($patch + 1))
    }
    return ((Compare-SemVer $target $base) -ge 0) -and ((Compare-SemVer $target $upper) -lt 0)
  }

  $isWild = ($rhs -match '\*') -or ($rhs -match '[xX]') -or ($count -lt 3)
  if ($isWild) {
    if ($count -le 1) {
      $lo = @($major, 0, 0)
      $hi = @(($major + 1), 0, 0)
    }
    else {
      $lo = @($major, $minor, 0)
      $hi = @($major, ($minor + 1), 0)
    }
  }
  else {
    $lo = $base
    $hi = $base
  }
  $exact = (Compare-SemVer $lo $hi) -eq 0

  switch ($op) {
    '>=' { return (Compare-SemVer $target $lo) -ge 0 }
    '>' { return (Compare-SemVer $target $hi) -gt 0 }
    '<=' { return (Compare-SemVer $target $hi) -le 0 }
    '<' { return (Compare-SemVer $target $lo) -lt 0 }
    default {
      if ($exact) { return (Compare-SemVer $target $lo) -eq 0 }
      return ((Compare-SemVer $target $lo) -ge 0) -and ((Compare-SemVer $target $hi) -lt 0)
    }
  }
}

# ---------------------------------------------------------------- Forge API (cached, polite)
$script:ApiCalls = 0
$script:CacheHits = 0

function Get-CachePath([string]$url) {
  $sha = [System.Security.Cryptography.SHA1]::Create()
  $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($url))
  $hex = -join ($hash | ForEach-Object { $_.ToString('x2') })
  return (Join-Path $CacheDir ("{0}.json" -f $hex))
}

function Invoke-ForgeApi([string]$path) {
  $url = "$ApiBase/$path"
  $cacheFile = Get-CachePath $url
  if (-not $NoCache -and (Test-Path -LiteralPath $cacheFile)) {
    $script:CacheHits++
    return (Get-Content -LiteralPath $cacheFile -Raw | ConvertFrom-Json)
  }
  $lastErr = $null
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
      $resp = Invoke-WebRequest -Uri $url -Method Get -TimeoutSec 30 `
        -UserAgent "spt-coop-installer manifest/refresh.ps1" -UseBasicParsing
      $text = $resp.Content
      if (-not $NoCache) {
        if (-not (Test-Path -LiteralPath $CacheDir)) { New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null }
        [System.IO.File]::WriteAllText($cacheFile, $text)
      }
      $script:ApiCalls++
      Start-Sleep -Milliseconds 200
      return ($text | ConvertFrom-Json)
    }
    catch {
      $lastErr = $_
      if ($attempt -lt 3) { Start-Sleep -Seconds (2 * $attempt) }
    }
  }
  Fail "Forge API request failed: $url`n  $($lastErr.Exception.Message)"
}

function Get-ForgeEntity([string]$kind, [int]$id) {
  $resp = Invoke-ForgeApi ("{0}/{1}" -f $kind, $id)
  if (-not $resp -or -not $resp.success -or -not $resp.data) { return $null }
  return $resp.data
}

function Get-ForgeVersions([string]$kind, [int]$id) {
  $all = New-Object System.Collections.Generic.List[object]
  $page = 1
  while ($true) {
    $resp = Invoke-ForgeApi ("{0}/{1}/versions?per_page=50&page={2}" -f $kind, $id, $page)
    if (-not $resp -or -not $resp.success) { return $all }
    if ($resp.data) { foreach ($v in $resp.data) { $all.Add($v) } }
    $lastPage = 1
    if ($resp.PSObject.Properties.Name -contains 'meta' -and $resp.meta -and $resp.meta.last_page) {
      $lastPage = [int]$resp.meta.last_page
    }
    if ($page -ge $lastPage -or $page -ge 20) { break }
    $page++
  }
  return $all
}

# ---------------------------------------------------------------- manifest rows
function Test-RowFormat([string]$row, [int]$index) {
  $problems = New-Object System.Collections.Generic.List[string]
  if ($null -eq $row -or $row.Trim() -eq '') { return @("row $index is empty") }
  $f = $row -split '\|'
  if ($f.Count -ne 4) { $problems.Add("row $index has $($f.Count) fields, expected 4 (id|kind|slug|version): '$row'") }
  if ($f.Count -ge 1 -and ($f[0] -notmatch '^\d+$')) { $problems.Add("row $index id is not an integer: '$($f[0])'") }
  if ($f.Count -ge 2 -and ($f[1] -notin @('mod', 'addon'))) { $problems.Add("row $index kind must be mod|addon: '$($f[1])'") }
  if ($f.Count -ge 3 -and [string]::IsNullOrWhiteSpace($f[2])) { $problems.Add("row $index slug is empty") }
  if ($f.Count -ge 4 -and ($f[3] -notmatch '^\d+\.\d+\.\d+$')) { $problems.Add("row $index version is not x.y.z: '$($f[3])'") }
  return $problems
}

function Read-Manifest([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) { Fail "manifest not found: $path" }
  try { $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
  catch { Fail "manifest is not valid JSON: $path`n  $($_.Exception.Message)" }
  if (-not ($json.PSObject.Properties.Name -contains 'mods')) { Fail "manifest has no 'mods' array: $path" }
  $problems = New-Object System.Collections.Generic.List[string]
  $i = 0
  foreach ($row in $json.mods) {
    $i++
    foreach ($p in (Test-RowFormat $row $i)) { $problems.Add($p) }
  }
  if ($problems.Count -gt 0) {
    Say "Manifest row validation failed ($($problems.Count) problem(s)):" "Red"
    foreach ($p in $problems) { Say "  - $p" "Red" }
    exit 1
  }
  $target = $SptTarget
  if (-not $target -and ($json.PSObject.Properties.Name -contains 'spt_target')) { $target = [string]$json.spt_target }
  if (-not $target) { $target = "4.1.6" }
  return [pscustomobject]@{
    Path     = $path
    Rows     = @($json.mods)
    Target   = $target
    SptTarget= $(if ($json.PSObject.Properties.Name -contains 'spt_target') { [string]$json.spt_target } else { $target })
    Generated= $(if ($json.PSObject.Properties.Name -contains 'generated') { [string]$json.generated } else { "" })
    Source   = $(if ($json.PSObject.Properties.Name -contains 'source') { [string]$json.source } else { "manifest/refresh.ps1" })
  }
}

function ConvertTo-Row($row) {
  return ("{0}|{1}|{2}|{3}" -f $row.Id, $row.Kind, $row.Slug, $row.Version)
}

# ---------------------------------------------------------------- live-set diff
function Invoke-LiveSetDiff([string]$path, $manifest) {
  if (-not (Test-Path -LiteralPath $path)) { Fail "live-set not found: $path" }
  try { $live = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
  catch { Fail "live-set is not valid JSON: $path`n  $($_.Exception.Message)" }

  if (-not ($live.PSObject.Properties.Name -contains 'mapping')) { Fail "live-set has no 'mapping' object: $path" }
  $server = @(); if ($live.PSObject.Properties.Name -contains 'server_mods') { $server = @($live.server_mods) }
  $client = @(); if ($live.PSObject.Properties.Name -contains 'client_plugins') { $client = @($live.client_plugins) }

  $engine = New-Object System.Collections.Generic.List[string]
  $unmapped = New-Object System.Collections.Generic.List[string]
  $liveIds = @{}
  foreach ($name in @($server) + @($client)) {
    $entry = $live.mapping.PSObject.Properties[$name]
    if (-not $entry) { $unmapped.Add($name); continue }
    $m = $entry.Value
    if ($m.PSObject.Properties.Name -contains 'engine' -and $m.engine) { $engine.Add($name); continue }
    if ($null -eq $m.id) { $unmapped.Add($name); continue }
    $liveIds[[int]$m.id] = @{ Name = $name; Slug = [string]$m.slug; Kind = [string]$m.kind }
  }

  $pinnedIds = @{}
  foreach ($row in $manifest.Rows) { $pinnedIds[[int](($row -split '\|')[0])] = ($row -split '\|')[2] }

  Say ""
  Say "LIVE-SET DIFF  ($path vs $($manifest.Path))" "Cyan"
  Say ("  server_mods: {0}   client_plugins: {1}   mapping entries used: {2}" -f $server.Count, $client.Count, $liveIds.Count)
  Say ("  pinned rows: {0}" -f $manifest.Rows.Count)
  Say (("  engine-provided (skipped): {0}  -> {1}" -f $engine.Count, ($engine -join ', ')))
  if ($unmapped.Count -gt 0) {
    Say (("  UNMAPPED live names ({0}) - extend the mapping in the live-set JSON:" -f $unmapped.Count)) "Yellow"
    foreach ($u in $unmapped) { Say "    ! $u" "Yellow" }
  }

  $add = @()
  foreach ($id in ($liveIds.Keys | Sort-Object)) { if (-not $pinnedIds.ContainsKey($id)) { $add += $liveIds[$id] } }
  $remove = @()
  foreach ($id in ($pinnedIds.Keys | Sort-Object)) { if (-not $liveIds.ContainsKey($id)) { $remove += [pscustomobject]@{ Id = $id; Slug = $pinnedIds[$id] } } }

  Say ""
  Say (("  ADD (live but not pinned): {0}" -f $add.Count)) "Green"
  foreach ($a in $add) { Say ("    + {0,-60} {1}" -f $a.Slug, $a.Name) "Green" }
  Say (("  REMOVE (pinned but not live): {0}" -f $remove.Count)) "Yellow"
  foreach ($r in $remove) { Say ("    - {0,-60} id={1}" -f $r.Slug, $r.Id) "Yellow" }
  $addonRemovals = @($manifest.Rows | Where-Object { ($_ -split '\|')[1] -eq 'addon' })
  if ($remove.Count -gt 0 -and $remove.Count -eq $addonRemovals.Count) {
    Say "  note: every removed row is an addon - Forge addons unpack into the parent mod's folder, so they never show up as their own directory." "DarkGray"
  }
  Say ""

  if ($unmapped.Count -gt 0) {
    Write-Host "REFRESH FAILED: live names with no mapping entry (see list above)" -ForegroundColor Red
    exit 1
  }
}

# ---------------------------------------------------------------- pin block rewriting
function New-PinLines($rows) {
  $lines = New-Object System.Collections.Generic.List[string]
  for ($i = 0; $i -lt $rows.Count; $i++) {
    $comma = if ($i -lt $rows.Count - 1) { "," } else { "" }
    $lines.Add(('  "{0}"{1}' -f $rows[$i], $comma))
  }
  return $lines
}

function Update-PinBlock([string]$path, $rows, [switch]$AddMarkers) {
  if (-not (Test-Path -LiteralPath $path)) { Fail "script not found: $path" }
  $raw = [System.IO.File]::ReadAllText($path)
  $nl = if ($raw -match "`r`n") { "`r`n" } else { "`n" }
  $lines = New-Object System.Collections.Generic.List[string]
  foreach ($l in ($raw -split '\r?\n')) { $lines.Add($l) }

  $startIdx = -1; $endIdx = -1
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($startIdx -lt 0 -and $lines[$i].StartsWith('# >>> GENERATED-MANIFEST')) { $startIdx = $i }
    elseif ($startIdx -ge 0 -and $lines[$i].StartsWith('# <<< GENERATED-MANIFEST')) { $endIdx = $i; break }
  }

  if ($startIdx -lt 0 -or $endIdx -lt 0) {
    if (-not $AddMarkers) { Fail "GENERATED-MANIFEST markers not found in $path" }
    # locate the $ModManifest = @( ... ) block and wrap it
    $modStart = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*\$ModManifest\s*=\s*@\(') { $modStart = $i; break } }
    if ($modStart -lt 0) { Fail "`$ModManifest = @( ... ) block not found in $path" }
    $modEnd = -1
    for ($i = $modStart + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*\)\s*$') { $modEnd = $i; break } }
    if ($modEnd -lt 0) { Fail "closing ')' for `$ModManifest not found in $path" }

    $insertAt = $modStart
    if ($insertAt -gt 0 -and $lines[$insertAt - 1] -match '^\s*#\s*id\|kind\|slug\|version') { $insertAt = $insertAt - 1 }
    $lines.Insert($insertAt, $MarkerStart)
    $modEnd = $modEnd + 1
    $lines.Insert($modEnd + 1, $MarkerEnd)
    $startIdx = $insertAt
    $endIdx = $modEnd + 1
  }

  if ($lines[$startIdx] -ne $MarkerStart) { $lines[$startIdx] = $MarkerStart }
  if ($lines[$endIdx] -ne $MarkerEnd) { $lines[$endIdx] = $MarkerEnd }

  $openIdx = -1
  for ($i = $startIdx; $i -le $endIdx; $i++) { if ($lines[$i] -match '^\s*\$ModManifest\s*=\s*@\(') { $openIdx = $i; break } }
  if ($openIdx -lt 0) { Fail "`$ModManifest = @( ... ) not found inside the GENERATED-MANIFEST block of $path" }
  $closeIdx = -1
  for ($i = $openIdx + 1; $i -le $endIdx; $i++) { if ($lines[$i] -match '^\s*\)\s*$') { $closeIdx = $i; break } }
  if ($closeIdx -lt 0) { Fail "closing ')' not found inside the GENERATED-MANIFEST block of $path" }

  $pinLines = New-PinLines $rows
  $newLines = New-Object System.Collections.Generic.List[string]
  for ($i = 0; $i -le $openIdx; $i++) { $newLines.Add($lines[$i]) }
  foreach ($p in $pinLines) { $newLines.Add($p) }
  for ($i = $closeIdx; $i -lt $lines.Count; $i++) { $newLines.Add($lines[$i]) }

  $out = ($newLines -join $nl)
  $utf8 = New-Object System.Text.UTF8Encoding($false)
  $changed = ($out -ne $raw)
  if ($changed) { [System.IO.File]::WriteAllText($path, $out, $utf8) }
  return $changed
}

function Write-ManifestJson([string]$path, $rows, [string]$target, [string]$generated, [string]$source) {
  $nl = "`n"
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.Append("{").Append($nl)
  [void]$sb.Append('  "generated": "').Append($generated).Append('",').Append($nl)
  [void]$sb.Append('  "spt_target": "').Append($target).Append('",').Append($nl)
  [void]$sb.Append('  "source": "').Append($source).Append('",').Append($nl)
  [void]$sb.Append('  "mods": [').Append($nl)
  for ($i = 0; $i -lt $rows.Count; $i++) {
    $comma = if ($i -lt $rows.Count - 1) { "," } else { "" }
    [void]$sb.Append('    "').Append($rows[$i]).Append('"').Append($comma).Append($nl)
  }
  [void]$sb.Append("  ]").Append($nl).Append("}").Append($nl)
  $out = $sb.ToString()
  $old = if (Test-Path -LiteralPath $path) { [System.IO.File]::ReadAllText($path) } else { "" }
  if ($out -ne $old) {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $out, $utf8)
    return $true
  }
  return $false
}

# ---------------------------------------------------------------- main
$manifest = Read-Manifest $ManifestPath
$targetTuple = Get-SemVer $manifest.Target
if (-not $targetTuple) { Fail "SPT target is not a version: '$($manifest.Target)'" }

if ($LiveSet) {
  Invoke-LiveSetDiff $LiveSet $manifest
  exit 0
}

Say ""
Say ("Manifest: {0}" -f $manifest.Path)
Say ("SPT target: {0}   rows: {1}   mode: {2}" -f $manifest.Target, $manifest.Rows.Count, $(if ($Apply) { "APPLY" } elseif ($Write) { "WRITE" } else { "dry run" }))
Say ""

$results = New-Object System.Collections.Generic.List[object]
$errors = New-Object System.Collections.Generic.List[string]
$index = 0
foreach ($row in $manifest.Rows) {
  $index++
  $f = $row -split '\|'
  $id = [int]$f[0]; $kind = $f[1]; $slug = $f[2]; $pinned = $f[3]

  $entity = Get-ForgeEntity $kind $id
  if (-not $entity) {
    $errors.Add("row $index ($row): Forge $kind/$id not found")
    continue
  }
  if ($entity.slug -ne $slug) {
    $errors.Add("row $index ($row): Forge $kind/$id is '$($entity.slug)', manifest says '$slug'")
    continue
  }

  $versions = Get-ForgeVersions $kind $id
  $usable = @()
  foreach ($v in $versions) {
    if ($v.version -notmatch '^\d+\.\d+\.\d+$') { continue }
    if ($kind -eq 'addon') {
      $usable += $v          # addons carry mod_version_constraint, not spt_version_constraint
    }
    elseif (Test-SptConstraint ([string]$v.spt_version_constraint) $targetTuple) {
      $usable += $v
    }
  }

  $best = $null
  foreach ($v in $usable) {
    if (-not $best) { $best = $v; continue }
    if ((Compare-SemVer (Get-SemVer $v.version) (Get-SemVer $best.version)) -gt 0) { $best = $v }
  }

  $status = "OK"
  $chosen = $pinned
  $note = ""
  if (-not $best) {
    $status = "KEPT-PINNED"
    $note = "no version declares SPT $($manifest.Target) support"
  }
  else {
    $chosen = $best.version
    if ($kind -eq 'addon') {
      $addonCons = [string]$best.mod_version_constraint
      $status = "OK-UNCONSTRAINED"
      $note = "addon: no spt constraint (Forge addons use mod_version_constraint '$addonCons')"
    }
    if ($chosen -ne $pinned) {
      if ($kind -eq 'addon') { $status = "BUMP-UNCONSTRAINED" } else { $status = "BUMP" }
      if ($kind -eq 'addon') {
        $note = ("{0} -> {1}  (highest version; addons carry no spt constraint)" -f $pinned, $chosen)
      }
      else {
        $note = ("{0} -> {1}  (constraint {2})" -f $pinned, $chosen, $best.spt_version_constraint)
      }
    }
    elseif ($kind -ne 'addon') {
      $note = ("constraint {0}" -f $best.spt_version_constraint)
    }
  }

  # the installer builds this URL; make sure Forge agrees with it
  if ($best -and $best.link) {
    $want = "/$kind/download/$id/$($entity.slug)/$($best.version)"
    if (-not ($best.link -like "*$want")) {
      $note = $note + "  URL-MISMATCH(forgem:$($best.link))"
    }
  }

  $results.Add([pscustomobject]@{
      Index  = $index
      Id     = $id
      Kind   = $kind
      Slug   = $slug
      Name   = [string]$entity.name
      Pinned = $pinned
      Chosen = $chosen
      Status = $status
      Note   = $note
      Versions = $versions.Count
    })
}

if ($errors.Count -gt 0) {
  Say "Row validation failed ($($errors.Count) problem(s)):" "Red"
  foreach ($e in $errors) { Say "  - $e" "Red" }
  exit 1
}

foreach ($r in $results) {
  $color = switch -Wildcard ($r.Status) {
    'BUMP*' { "Yellow" }
    'KEPT-PINNED' { "Magenta" }
    'OK-UNCONSTRAINED' { "DarkGray" }
    default { "Gray" }
  }
  Say ("  {0,-18} {1,5} {2,-58} {3,-9} {4}" -f $r.Status, $r.Id, $r.Slug, $r.Pinned, $r.Note) $color
}

$bumps = @($results | Where-Object { $_.Status -like 'BUMP*' })
$kept = @($results | Where-Object { $_.Status -eq 'KEPT-PINNED' })
Say ""
Say ("Drift: {0} bump(s), {1} kept-pinned (no eligible version), {2} unchanged (Forge calls: {3}, cache hits: {4})" -f `
    $bumps.Count, $kept.Count, ($results.Count - $bumps.Count - $kept.Count), $script:ApiCalls, $script:CacheHits) `
  $(if ($bumps.Count -gt 0) { "Yellow" } else { "Green" })
foreach ($k in $kept) {
  Say ("  WARNING: {0} (id {1}) has no Forge version that accepts SPT {2}; keeping the pinned {3}. Check manually." -f `
      $k.Slug, $k.Id, $manifest.Target, $k.Pinned) "Magenta"
}

$newRows = New-Object System.Collections.Generic.List[string]
foreach ($r in $results) { $newRows.Add(("{0}|{1}|{2}|{3}" -f $r.Id, $r.Kind, $r.Slug, $r.Chosen)) }

if (-not ($Write -or $Apply)) {
  Say ""
  Say "Dry run only - nothing written. Use -Write to update manifest/mods.json, -Apply to also rewrite the pin blocks." "Cyan"
  Say ""
  exit 0
}

$generated = if ($manifest.Generated) { $manifest.Generated } else { (Get-Date).ToString('yyyy-MM-dd') }
$changedManifest = Write-ManifestJson $ManifestPath $newRows $manifest.SptTarget $generated $manifest.Source
Say ("manifest/mods.json: {0}" -f $(if ($changedManifest) { "updated" } else { "unchanged" })) "Green"

if ($Apply) {
  $c1 = Update-PinBlock $SetupScript $newRows
  Say ("setup-spt.ps1: {0}" -f $(if ($c1) { "pin block rewritten" } else { "no change" })) "Green"
  $c2 = Update-PinBlock $ModsScript $newRows -AddMarkers
  Say ("install-mods.ps1: {0}" -f $(if ($c2) { "pin block rewritten" } else { "no change" })) "Green"
}

Say ""
Say ("Done: {0} rows pinned to SPT {1}." -f $newRows.Count, $manifest.Target) "Green"
Say ""
exit 0
