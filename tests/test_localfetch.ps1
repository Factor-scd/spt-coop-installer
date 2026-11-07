$ErrorActionPreference = "Continue"
$src = "C:\temp\setup-spt.ps1"
$txt = [System.IO.File]::ReadAllText($src)
$m = [regex]::Match($txt, '(?s)function Get-File\(.*?\n\}')
if (-not $m.Success) { Write-Output "BLOCK-NOT-FOUND"; exit 1 }
if ($m.Value -notmatch "using local copy") { Write-Output "BLOCK-MISSING-LOCALBRANCH"; exit 1 }
Write-Output ("BLOCK-EXTRACTED len=" + $m.Value.Length)
$gf = Join-Path $PSScriptRoot "gf-def.ps1"
[System.IO.File]::WriteAllText($gf, $m.Value)
$script:log = @()
function Say($m2, $c) { $script:log += $m2 }
function Die($m2, $c) { throw ("DIE:" + $m2) }
. $gf

$root = $PSScriptRoot
$cacheDir = Join-Path $root "cache"
$dead = "http://127.0.0.1:9/nope"
$fixtures = @("SPT-4.1.6.7z", "SPT-4.1.6-40743-731d7a2.7z", "quest-glow.zip", "gf-def.ps1", "SPT-4.2.0-99999.7z", "SPT-v4.1.6.7z", "SPT-Release-4.1.6.7z")

function Reset-Fixtures {
  foreach ($f in $script:fixtures) { Remove-Item (Join-Path $script:root $f) -Force -ErrorAction SilentlyContinue }
  Remove-Item (Join-Path $script:root "offline") -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item (Join-Path $script:root "spt-setup-cache") -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $script:cacheDir -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force -Path $script:cacheDir | Out-Null
  $script:log = @()
}
$script:fails = 0
function Check($name, $cond) {
  if ($cond) { Write-Output ("PASS " + $name) } else { Write-Output ("FAIL " + $name); $script:fails++ }
}

Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.1.6.7z"), "AAA")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
Check "T1-exact-toplevel" (([System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z")) -eq "AAA") -and (($script:log -join "|") -match "using local copy"))

Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.1.6-40743-731d7a2.7z"), "BBB")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
Check "T2-prefix-toplevel" (([System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z")) -eq "BBB") -and (($script:log -join "|") -match "using local copy"))

Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.1.6.7z"), "AAA")
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.1.6-40743-731d7a2.7z"), "BBB")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
Check "T2b-exact-beats-fuzzy" ([System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z")) -eq "AAA")

Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.1.6.7z"), "AAA")
[System.IO.File]::WriteAllText((Join-Path $cacheDir "SPT-4.1.6.7z"), "CCC")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
Check "T3-cache-wins" (([System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z")) -eq "CCC") -and (($script:log -join "|") -match "cached:"))

Reset-Fixtures
$died = $false
try { Get-File $dead (Join-Path $cacheDir "missing-mod-1.0.0.zip") } catch { if ("$_" -match "DIE:download failed") { $died = $true } }
Check "T4-dead-url-dies-clean" $died

Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "quest-glow.zip"), "EEE")
Get-File $dead (Join-Path $cacheDir "quest-glow-1.0.0.zip")
Check "T5-fuzzy-mod-name" (([System.IO.File]::ReadAllText((Join-Path $cacheDir "quest-glow-1.0.0.zip")) -eq "EEE") -and (($script:log -join "|") -match "using local copy"))

Reset-Fixtures
$died2 = $false
try { Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z") } catch { if ("$_" -match "DIE:download failed") { $died2 = $true } }
Check "T6-extension-guard" $died2

Reset-Fixtures
New-Item -ItemType Directory -Force -Path (Join-Path $root "offline") | Out-Null
[System.IO.File]::WriteAllText((Join-Path $root "offline\SPT-4.1.6.7z"), "GGG")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
Check "T7-subfolder-exact" (([System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z")) -eq "GGG") -and (($script:log -join "|") -match "using local copy"))

Reset-Fixtures
New-Item -ItemType Directory -Force -Path (Join-Path $root "spt-setup-cache") | Out-Null
[System.IO.File]::WriteAllText((Join-Path $root "spt-setup-cache\SPT-4.1.6-40743-731d7a2.7z"), "HHH")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
Check "T8-subfolder-fuzzy" (([System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z")) -eq "HHH") -and (($script:log -join "|") -match "using local copy"))

Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-4.2.0-99999.7z"), "ZZZ")
$died3 = $false
try { Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z") } catch { if ("$_" -match "DIE:download failed") { $died3 = $true } }
Check "T9-miss-forensics" ($died3 -and (($script:log -join "|") -match "archives found.*SPT-4\.2\.0"))

Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-v4.1.6.7z"), "VVV")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
Check "T10-renamed-hyphen-version" (([System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z")) -eq "VVV") -and (($script:log -join "|") -match "using local copy"))

Reset-Fixtures
[System.IO.File]::WriteAllText((Join-Path $root "SPT-Release-4.1.6.7z"), "RRR")
Get-File $dead (Join-Path $cacheDir "SPT-4.1.6.7z")
Check "T11-renamed-release-word" (([System.IO.File]::ReadAllText((Join-Path $cacheDir "SPT-4.1.6.7z")) -eq "RRR") -and (($script:log -join "|") -match "using local copy"))

Reset-Fixtures
if ($script:fails -gt 0) { Write-Output ("LOCALFETCH-FAILS=" + $script:fails); exit 1 }
Write-Output "LOCALFETCH-11/11"
