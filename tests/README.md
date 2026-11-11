# Tests

## Local-fetch behavioural harness — `test_localfetch.ps1`

`tests/test_localfetch.ps1` is the gate for the download/local-archive logic in
`setup-spt.ps1`'s `Get-File`. It copies the real script to the literal path
`C:\temp\setup-spt.ps1`, extracts the `Get-File` function from the file text, and runs
eleven cases (T1..T11) against stub archives: cache-wins, exact/prefix/version-token
matching, subfolder search, the extension guard, and the forensics line on a miss.
It exits 1 unless it prints `LOCALFETCH-11/11`.

### One harness, both platforms

| platform | how to run | `curl.exe` |
| --- | --- | --- |
| Windows | `powershell -ExecutionPolicy Bypass -File tests\test_localfetch.ps1` | the real Windows `curl.exe` |
| Linux | `bash tests/run-tests.sh` | `tests/shim/curl.exe` (a `sh` wrapper around `/usr/bin/curl`) |

The shim exists only so the Linux/pwsh box can exercise the Windows code path: the
scripts call `curl.exe`, which Linux does not have, so the dead-URL cases (T4, T6, T9)
would die with `CommandNotFound` instead of the clean `download failed: ...` die.
`run-tests.sh` prepends `tests/shim` to `PATH` for the harness run and nothing else.

**8/11 without the shim is an environment artefact, not a code failure.** Those three
cases (`T4-dead-url-dies-clean`, `T6-extension-guard`, `T9-miss-forensics`) fail
identically on every commit on a Linux box with no shim, because there is no
`curl.exe` to fail cleanly.

`run-tests.sh` also builds the fixture (copies `setup-spt.ps1` to the literal filename
`C:\temp\setup-spt.ps1`, backslashes and all — legal on Linux) and cleans up afterwards:
the fixture, `tests/cache/`, `tests/gf-def.ps1`, and the archive files/directories the
harness drops here. It exits 0 only when the output contains `LOCALFETCH-11/11`.

pwsh is not necessarily on `PATH` on the build box; `run-tests.sh` defaults to
`/tmp/opencode/pwsh/pwsh` and honours `PWSH=/path/to/pwsh`.

### Notes

- The harness reads the script source, not the AST: `Get-File` must keep the literal
  signature `function Get-File($url, $dest)` and must not contain a line consisting of a
  bare `}` (the extraction regex stops at the first one).
- Do not rename the success log lines (`cached:`, `using local copy:`, `downloading`) or
  the `archives found: ...` miss diagnostic — the cases assert on them.
