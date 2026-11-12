#!/usr/bin/env bash
# Run the local-fetch behavioural harness (tests/test_localfetch.ps1) on any platform.
#
# The harness copies the real setup-spt.ps1 to the literal path it codes for
# (`C:\temp\setup-spt.ps1` - a legal filename on Linux), extracts the Get-File
# function from it and exercises the cache/local-archive/download code paths.
#
# On Windows the scripts' `curl.exe` is the real one. On Linux it is not, so we put
# tests/shim first on PATH (a sh script that execs /usr/bin/curl). Without that shim
# the run still produces 8/11 - that is an environment artefact, not a code failure.
#
# Usage:  bash tests/run-tests.sh
# Exit:   0 only when the harness prints LOCALFETCH-11/11.

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || exit 1

PWSH="${PWSH:-/tmp/opencode/pwsh/pwsh}"
FIXTURE='C:\temp\setup-spt.ps1'

if [ ! -x "$PWSH" ]; then
  echo "run-tests: pwsh not found at $PWSH (override with PWSH=/path/to/pwsh)" >&2
  exit 1
fi

cleanup() {
  rm -f "$REPO_ROOT/$FIXTURE"
  rm -rf "$REPO_ROOT/tests/cache"
  rm -f "$REPO_ROOT/tests/gf-def.ps1"
  # fixture archives/folders the harness drops in tests/
  rm -f "$REPO_ROOT/tests/SPT-4.1.6.7z" \
        "$REPO_ROOT/tests/SPT-4.1.6-40743-731d7a2.7z" \
        "$REPO_ROOT/tests/SPT-4.2.0-99999.7z" \
        "$REPO_ROOT/tests/SPT-v4.1.6.7z" \
        "$REPO_ROOT/tests/SPT-Release-4.1.6.7z" \
        "$REPO_ROOT/tests/quest-glow.zip" \
        "$REPO_ROOT/tests/quest-glow-1.0.0.zip"
  rm -rf "$REPO_ROOT/tests/offline" "$REPO_ROOT/tests/spt-setup-cache"
}
trap cleanup EXIT

cleanup
cp setup-spt.ps1 "$FIXTURE" || exit 1
export PATH="$REPO_ROOT/tests/shim:$PATH"

out="$("$PWSH" -NoProfile -File tests/test_localfetch.ps1 2>&1)"
rc=$?
printf '%s\n' "$out"

if ! printf '%s\n' "$out" | grep -q 'LOCALFETCH-11/11'; then
  echo "run-tests: harness did not report LOCALFETCH-11/11 (pwsh exit $rc)" >&2
  exit 1
fi

# Host-side regression tests (argument quoting round-trip + target validation ordering).
# Pure BCL code, so it runs on the Linux build host with nothing but the SDK.
if command -v dotnet >/dev/null 2>&1; then
  argv_out="$(dotnet run --project tests/argvtests -c Release 2>&1)"
  argv_rc=$?
  printf '%s\n' "$argv_out"
  if ! printf '%s\n' "$argv_out" | grep -q '^ARGV-'; then
    echo "run-tests: argument tests did not reach their verdict (dotnet exit $argv_rc)" >&2
    exit 1
  fi
  if printf '%s\n' "$argv_out" | grep -q '^ARGV-FAILED'; then
    echo "run-tests: argument tests FAILED" >&2
    exit 1
  fi
  # Every host-side suite must have actually run: a suite that silently stopped being invoked
  # would otherwise leave the verdict green with fewer checks.
  for suite in '^PASS game/' '^PASS layout/' '^PASS runlog/' '^PASS D3/'; do
    if ! printf '%s\n' "$argv_out" | grep -q "$suite"; then
      echo "run-tests: $suite suite did not run" >&2
      exit 1
    fi
  done
else
  echo "run-tests: dotnet not found - argument tests SKIPPED (this is not a pass)" >&2
  exit 1
fi

exit 0
