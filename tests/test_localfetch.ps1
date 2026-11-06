$ErrorActionPreference = "Continue"
$src = "C:\temp\setup-spt.ps1"
$txt = [System.IO.File]::ReadAllText($src)
$m = [regex]::Match($txt, '(?s)function Get-File\(.*?\n\}')
if (-not $m.Success) { Write-Output "BLOCK-NOT-FOUND"; exit 1 }
Write-Output ("BLOCK-EXTRACTED len=" + $m.Value.Length)
if ($m.Value -notmatch "using local copy") { Write-Output "BLOCK-MISSING-LOCALBRANCH"; exit 1 }

# materialize the shipped block as a FILE so the function gets file-defined semantics
# (IEX-defined functions do not inherit $PSScriptRoot; production defines it in-file)
$gf = Join-Path $PSScriptRoot "gf-def.ps1"
[System.IO.File]::WriteAllText($gf, $m.Value)

$script:log = @()
function Say($m2, $c) { $script:log += $m2 }
function Die($m2, $c) { throw ("DIE:" + $m2) }
. $gf

$root = $PSScriptRoot
$cacheDir = Join-Path $root "cache"
$dead = "http://127.0.0.1:9/nope"
$fixtures = @("SPT-4.1.6.7z", "SPT-4.1.6-40743-731d7a2.7z", "quest-glow.zip", "gf-def.ps1")

function Reset-Fixtures {
  foreach ($f in $script:fixtures) { Remove-Item (Join-Path $script:root $f) -Force -ErrorAction SilentlyContinue }
  Remove-Item $script:cacheDir -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force -Path $script:cacheDir | Out-Null
  $script:log = @()
}

$script:fails = 0
function Check($name, $cond) {
  if ($cond) { Write-Output ("PASS " + $name) } else { Write-Output ("FAIL " + $name); $script:fails++ }
}

# T1: exact normalized-name match (SPT-4.1.6.7z dropped beside the script)
Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.1.6.7z"), "AAA")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
$d1 = [System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z"))
Check "T1-exact-name-used" (($d1 -eq "AAA") -and (($script:log -join "|") -match "using local copy"))

# T2: longer upstream name serves the short dest (prefix match)
Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.1.6-40743-731d7a2.7z"), "BBB")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
$d2 = [System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z"))
Check "T2-prefix-name-used" (($d2 -eq "BBB") -and (($script:log -join "|") -match "using local copy"))

# T2b: exact beats longer fuzzy when both exist
Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.1.6.7z"), "AAA")
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.1.6-40743-731d7a2.7z"), "BBB")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
$d2b = [System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z"))
Check "T2b-exact-beats-fuzzy" ($d2b -eq "AAA")

# T3: cache still wins over script folder
Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.1.6.7z"), "AAA")
[System.IO.File]::WriteAllText((Join-Path $cacheDir "SPT-4.1.6.7z"), "CCC")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
$d3 = [System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z"))
Check "T3-cache-wins" (($d3 -eq "CCC") -and (($script:log -join "|") -match "cached:"))

# T4: nothing local, dead url -> Die "download failed"
Reset-Fixtures
$died = $false
try { Get-File $dead (Join-Path $cacheDir "missing-mod-1.0.0.zip") } catch { if ("$_" -match "DIE:download failed") { $died = $true } }
Check "T4-dead-url-dies-clean" $died

# T5: fuzzy unversioned mod name (want quest-glow-1.0.0.zip, drop quest-glow.zip)
Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "quest-glow.zip"), "EEE")
Get-File $dead (Join-Path $cacheDir "quest-glow-1.0.0.zip")
$d5 = [System.IO.File]::ReadAllText((Join-Path $cacheDir "quest-glow-1.0.0.zip"))
Check "T5-fuzzy-mod-name" (($d5 -eq "EEE") -and (($script:log -join "|") -match "using local copy"))

# T6: wrong extension is NOT hijacked (this harness .ps1 must never serve a .7z dest)
Reset-Fixtures
$died2 = $false
try { Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z") } catch { if ("$_" -match "DIE:download failed") { $died2 = $true } }
Check "T6-extension-guard" $died2

Reset-Fixtures
if ($script:fails -gt 0) { Write-Output ("LOCALFETCH-FAILS=" + $script:fails); exit 1 }
Write-Output "LOCALFETCH-7/7"
