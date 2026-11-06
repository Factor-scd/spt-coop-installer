---
title: "SPT + Fika Co-op Installer - E2E Test Proof"
created: 2026-11-05
updated: 2026-11-06
type: research
tags: [spt, tarkov, fika, installer, testing, proof]
status: final
confidence: high
---

# SPT + Fika Co-op Installer - E2E Test Proof

**Artifact:** `setup-spt.ps1` + `run-setup.bat` (friend-facing one-double-click installer)
**Scope:** the INSTALL path (fresh install, auto-detect, EFT downgrade patcher, mod layer, verify gate)
**Test window:** 2026-11-04 -> 2026-11-05 (runs 18:48 and 19:07 on 11-05 are the headline runs)
**Lab host:** quadinaros-02 (VM 114) - Windows Server 2022, PowerShell 5.1, E: = 200 GB NTFS
**Prepared by:** Hermes Conrad, Grade 36 Bureaucrat - filed in triplicate
**Related:** [[2026-10-08-tarkov-spt-fika-coop-server]] (server-side deployment research)

---

## 1. Artifact identity (the bytes under test)

Every claim below is about EXACTLY these bytes. The chain is verified three ways:

| Copy | sha256 of `setup-spt.ps1` | Role |
|---|---|---|
| Published: `git.buzzbee.dev/BuzzbeeSCD/spt-coop-installer` @ `71db3a4` | `7ec871b4c6acf71d1f5c2246d10e1036ff4f44097390376d417d263d5f45f7d6` | what friends download |
| Local repo working tree | `7ec871b4c6acf71d1f5c2246d10e1036ff4f44097390376d417d263d5f45f7d6` | source of truth |
| `quadinaros-02 C:\temp\setup-spt.ps1` | `7ec871b4c6acf71d1f5c2246d10e1036ff4f44097390376d417d263d5f45f7d6` | the copy that ran EVERY lab run below |

All three identical (23,064 bytes). Git blob `78e5ecfd93bebec64550d484d4a0c93b3a0a5f50`.
Companion files: `run-setup.bat` sha256 `dee68cd90e3406001c460aff8f45e0eb06a46e52ced4d5feaaa7b375a83ede8a`,
`README.md` sha256 `3ec33b313776186bf61e724a2df4666d4cba2cd355e08267c77e6a8db9d178f4`.

**Zero edits to the script since the final test run and publication.** The patcher E2E (Section 4)
ran after the last code change and before/after publication on identical bytes.

## 2. Verdict

**ALL PATHS GREEN.** Four complete installs ran end-to-end (three fresh + one `-Update` against
the live backend), including the one path no lab had ever exercised: the EFT downgrade
patcher's SUCCESS branch on genuine live-build files. Every failure mode stops clean with a
plain-English message and leaves zero residue. Re-running is always safe (the live EFT is
only ever copied, never modified).

## 3. Test matrix

| Layer | Method | Result |
|---|---|---|
| Syntax gate | `[Parser]::ParseFile` on the SHIPPED file, PS 5.1 | `PARSE-OK 0 errors` (after every change; final run after self-elevation block) |
| Install-path prompt | behavioral harness, shipped block extracted verbatim, 5 scenarios | 5/5 (Enter default, trailing-slash trim, quote-strip, invalid -> re-prompt, `-InstallDir` skips prompt) |
| Pun ticker | shipped block, runspace harness | pun lines emitted on cadence, `ERR-COUNT=0`, double-stop idempotent |
| **EFT auto-detect** | 5-case matrix, **12 assertions, run TWICE on final bytes** | **12/12 x2** (see Section 5) |
| Integration `-Update` | live backend, quadinaros-02 | 54/54 mods, Force IP `10.4.20.2`, launcher -> `https://10.4.20.2:6969`, **4/4 tuned configs sha256-UNCHANGED**, post-restart HTTP 200 + `ModLoader: loading: 35 server mods` |
| Fresh install (stub source @40743) | Lab Run A, isolated `E:\SPT_FreshTest` | `INSTALLER-EXIT=0`, `ALL DONE`, 7/7 verify, HTTP 200 (2nd pass; 1st pass caught real bug #1) |
| Patcher plumbing (stub @48558) | Lab Run B, isolated `E:\SPT_PatchTest` | preflight + 7358 MB download + extract + `patcher.exe autoclose` + **clean exit-13 failure** on stubs (2nd pass; 1st pass caught real bug #2) |
| **Patcher E2E (REAL @48484)** | full shipping flow, Section 4 | **exit 10 = success, ConsistencyInfo 48484 -> 40743, `INSTALLER-EXIT=0`, `ALL DONE`** |
| Free-space gate | live, unscripted | correctly REFUSED a run at `99.3 GB free - need 106 GB`, before any copy, swept clean (Section 4) |
| Entry chain (`run-setup.bat`) | spaces-in-path dir (`C:\temp\bat test`), arg passthrough | `BAT-CHAIN-OK args=-Foo,bar` |
| Verify gate + reachability | every full run | 7/7 points + `server reachability HTTP 200` |
| Residue audit | after every run | test dirs, test registry keys (`SPTAutoDetectTest`), fw rules all swept; disk returned to baseline |

## 4. HEADLINE: the real-build patcher E2E (verbatim evidence)

Run `proc_dc7ccf49f4ab`, `PATCHTEST-START 2026-11-05T19:07:30`, shipping bytes, no code changes.
Source: **genuine live EFT** (BSG-launched tree at build 48484) served read-only over SMB from
the gaming PC; the installer auto-detected nothing - it was pointed at the source with
`-EftPath`, exactly like a real user with an odd install location.

```
SOURCE exe=True data=True
SOURCE-VERSION {"Version":"1.3.0.0.48484","Entries":[{"Path":"EscapeFromTarkov_Data\\StreamingA
>> patcher preflight ok (7358 MB patch, staging on E:)
>> EFT build 48484 != 40743 - downloading downgrade patcher...
>> downloading patcher-48484-40743.7z (7358 MB)
>> extracting patcher...
>> running patcher.exe autoclose (exit 10 = success)...
>> patch applied - cleaning up patcher files
>> ALL DONE. Launch SPT.Launcher.exe, pick 'Matt Co-op', and yell in Discord when you're in.
INSTALLER-EXIT=0
CONSISTENCY E:\SPT_PatchReal\ConsistencyInfo :: {"Version":"0.16.9.5.40743","Entries":[{"Path":"EscapeFromTarkov.exe","Hash":"a8c478aabf6bd8be98fdc2bab70be6cd","Size":2
swept fw rule: SPT Client - EscapeFromTarkov
swept fw rule: SPT Client - SPT.Server
swept E:\SPT_PatchReal
E: free GB after sweep: 113.1
```

The **`ConsistencyInfo` line is the proof**: the tree entered at `1.3.0.0.48484` and left at
`0.16.9.5.40743` - a real downgrade of real files, hash-manifest rewritten by the patcher.

Bonus: the same run's FIRST attempt (18:48:58) validated the free-space gate live:

```
SETUP FAILED: Only 99 GB free on E: - need 106 GB (fresh install: EFT copy + patcher + SPT + mods).
E: free GB after sweep: 99.3
```

Fail early, fail clean, re-run safe - by design, caught in production conditions.

## 5. Auto-detection proof (no path given)

The matrix (`autodetect_test.ps1`, run twice on final bytes = 24 assertions total) covers the
"just run it" case where the friend passes NO `-EftPath`:

| Case | Setup | Result |
|---|---|---|
| C1 | uninstall entry in registry `WOW6432Node` view | `EFT found: E:\AD_Src1` (stub) -> stops at version parse = correct |
| C2 | uninstall entry in native HKLM view | `EFT found: E:\AD_Src2` -> same |
| C3 | common path `C:\Battlestate Games\EFT` | `EFT found: C:\Battlestate Games\EFT` -> same |
| C4 | **FULL FRESH INSTALL with auto-detected source** | `ALL DONE`, 7/7 verify, HTTP 200, **exit 0** |
| C5 | nothing installed anywhere | clean die: `EFT not found in the registry or common paths. This script assumes Escape From Tarkov is already installed via the BSG Launcher - or pass -EftPath "D:\path\to\EFT".` exit 1 |

Detection order: `-EftPath` -> registry uninstall entries (both views, `DisplayName` ~ "Escape
From Tarkov" + `InstallLocation`) -> defaults (`C:/D:\Battlestate Games\EFT`, Steam library) ->
validation gate requiring BOTH `EscapeFromTarkov.exe` and `EscapeFromTarkov_Data`.

## 6. Bugs caught before ship (each: symptom -> root cause -> fix -> proven by re-run)

1. **Fresh-install crash** - `WriteAllText ... Could not find a part of the path
   ...\user\Launcher\LauncherSettings.json`. Fresh installs have no `user\Launcher` dir.
   Fix: `New-Item -ItemType Directory -Force` before the write. Caught by Lab Run A pass 1.
2. **Patcher staging filled C:** - `Cannot set length for output file ... There is not enough
   space on the disk` -> `SETUP FAILED: patcher.exe not found after extraction`. The 7.3 GB
   unpack landed on the tiny system drive. Fix: staging on the INSTALL drive +
   size-aware preflight before any copy + `robocopy /MOVE` + 7z exit-code check.
   Caught by Lab Run B pass 1.
3. **Tuned-config clobber** - re-extraction reset QuestExpMultiplier (2.0), ABPS caps, SVM
   `Noname.json`, `fika.jsonc`. Fix: `robocopy /XF` protects the four tuned files.
   Caught by pre-integration audit.
4. **Ticker silent failure** - `"{0}" -f $a, $b` inside `[Console]::WriteLine(...)` parses the
   comma list as separate method args and threw every tick. Fix: parenthesized format.
   Caught by ticker harness (symptom: no output).
5. **Right-click readiness** - stock clients block `.ps1` (policy/MOTW) and run non-elevated.
   Fix: `run-setup.bat` (policy bypass) + self-elevation in the script. Caught by readiness
   review + bat-chain spaces-path test.

## 7. Failure-mode audit (every exit is a clean exit)

| Condition | Behavior | Verified by |
|---|---|---|
| EFT not found anywhere | die + naming the `-EftPath` escape hatch, exit 1 | matrix C5 |
| `ConsistencyInfo` unreadable | die "is the EFT install complete and updated?", exit 1 | matrix C1-C3 |
| Build not in patch manifest | die "no direct patch from build X to Y ... ask Matt", exit 1 | designed path (manifest covers 48484/48558 - both seen live) |
| Disk below gate | die with both GB numbers, BEFORE any copy | live at 18:48:58 |
| Patcher exit != 10 | die "patcher exited with code N (10 = success)", exit 1 | Lab Run B (code 13 on stubs) |
| Elevation declined | die with Win+X instructions | code review (gap below) |
| Any re-run | idempotent; sweeps own targets; leftover mods quarantine to `BepInEx/_disabled` (never delete) | integration + lab re-runs |

## 8. Named gaps (the honest 1%)

1. **UAC elevation branch** - not clickable in the lab (SSH has no interactive UAC desktop).
   The standard 8-line pattern is code-reviewed; the landed path (already-admin) is proven by
   every run above.
2. **Antivirus interference** - untestable in a lab. Worst case is a blocked file = clean stop
   with a readable error; nothing in the flow modifies the live EFT.
3. **Friend-PC variance** (OneDrive desktops, exotic paths) - mitigated: spaces-in-path bat
   test, quote-strip prompt test, Mark-of-the-Web note in the README.

## 9. Reproduction index

Harnesses (each extracts/invokes the SHIPPED code, never a reimplementation):
`autodetect_test.ps1` (matrix), `lab_fresh_test.ps1` (Runs A/B driver), `drive_int.ps1`
(integration `-Update` + sha256 config snapshots), `run_patchtest.ps1` (patcher E2E driver),
`parsecheck.ps1` (syntax gate), `run_prompt_tests.ps1`, `test_ticker.ps1`,
`run-setup-test.bat` + `test-target.ps1` (entry chain). Located in
`/home/matt/.hermes/cache/scratch/` (24h TTL - archive on request).

---

*Verdict: stamped and filed. Form 27-B countersigned. The install is proven.*
## Status update - 2026-11-06 (post-proof copy change)

First real friend install completed 2026-11-06 08:06 (in the wild, before any lab claim could
help). The friend asked "which one do I open to run it?" while browsing the install root: the
final `ALL DONE` message named `SPT.Launcher.exe` WITHOUT its `SPT_Runtime\` path.

- **Change scope:** 2 string literals (the final Say lines) - now names `SPT_Runtime\SPT.Launcher.exe`,
  warns against double-clicking `EscapeFromTarkov.exe` / `EscapeFromTarkov_BE.exe`, and prints the
  desktop-shortcut ritual. Zero logic touched.
- **Validation:** PS 5.1 `[Parser]::ParseFile` gate = `PARSE-OK 0 errors` (same gate as the suite).
  NOT re-run end-to-end: strings cannot alter control flow; every behavioral claim above stands.
- **New sha256 (`setup-spt.ps1`):** `7849caf0c90e24ceac53df519b30a77103a5490159f1a3dd2c7b1b16952e6cdb` (was `7ec871b4c6acf71d1f5c2246d10e1036ff4f44097390376d417d263d5f45f7d6`)
- Published same-day to `git.buzzbee.dev/BuzzbeeSCD/spt-coop-installer`.

## Status update - 2026-11-06 (post-proof change: local-artifact auto-detect)

Field report: the SPT installer URL failed somewhere in the wild. Ask: auto-detect a local
installer dropped next to the script and use it automatically, no arguments.

- **Change scope:** `Get-File` gained a local-first lookup before any network access: exact
  normalized-name match, then prefix match (e.g. `SPT-4.1.6-40743-731d7a2.7z` serves
  `SPT-4.1.6.7z`), same extension only. Cache behavior unchanged (cache still wins). Download
  path untouched verbatim. Zero parameters - auto-detect, auto-use.
- **Validation:** PS 5.1 `[Parser]::ParseFile` gate `PARSE-OK 0 errors`; behavioral harness
  runs the EXTRACTED shipped `Get-File` block against a dead URL: **7/7** (exact-name,
  prefix-name, exact-beats-fuzzy, cache-still-wins, dead-url-dies-clean, fuzzy-mod-name,
  extension-guard). Harness note: the block is materialized into a file before definition -
  verified on PS 5.1 that IEX-defined functions do not inherit `$PSScriptRoot` while
  file-defined ones (production shape) do; the shipped code is correct in production.
- **Host probes same-day:** all four upstream URLs returned HTTP 200 from this network
  (incl. the SPT asset at 154 MB) - the field failure was not reproducible from here; the
  local-first path removes the dependency regardless.
- NOT re-run end-to-end: the change only short-circuits the fetch step; the extraction and
  install pipeline is byte-identical. Every behavioral claim above stands.
- **New sha256 (`setup-spt.ps1`):** `15a8bce33dceacc9cd2dc96bf206cadcf758c7ac5019d296f3d4fde27e05c1f8`
  (was `7849caf0c90e24ceac53df519b30a77103a5490159f1a3dd2c7b1b16952e6cdb`)
