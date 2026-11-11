# `manifest/` — the installer's mod pack, reconciled to the live server

Machine-readable mod list + the helper that regenerates it. The installers
(`setup-spt.ps1`, `install-mods.ps1`) read this list; nobody hand-edits the pins any more.

| file | what it is |
| --- | --- |
| `mods.json` | the pack. `{"generated","spt_target","source","mods":["<id>\|<kind>\|<slug>\|<version>", …]}` |
| `live-set.json` | what is actually installed on `quadinaros-02`, plus the live-name → Forge-id mapping |
| `refresh.ps1` | re-queries Forge, reports drift, rewrites the pins (`-Write` / `-Apply`) |
| `.cache/` | raw Forge responses used by `refresh.ps1` (gitignored) |

Row format is fixed: `"<forgeId>|<kind>|<slug>|<version>"`, `kind` is `mod` or `addon`, all four
fields non-empty. Download URL the installer builds: `https://sp-mod.com/$kind/download/$id/$slug/$version`
(checked against the Forge `link` field on every refresh).

## Counts

| | count |
| --- | --- |
| live server mods (`SPT_Runtime/user/mods`) | 38 folder names |
| live client plugins (`BepInEx/plugins`) | 41 folder names |
| distinct Forge entries behind those 79 names | 51 |
| engine-provided live names (not Forge mods) | 4 |
| **old pin list** | **54 rows** |
| **new pack** | **52 rows** (51 Forge entries + 1 addon) |

`54` was never a real number for this server — it was the count of a stale list. The new count is
what the live pack needs; the old list contained 9 mods the server does not run and missed 7 it does.

## Changes vs the old 54-row pin list

ADDED (7 — present on the live server, absent from the old pins):

| slug | Forge id | live folder(s) |
| --- | --- | --- |
| `gilded-key-storage` | 865 | `DrakiaXYZ-GildedKeyStorage` |
| `classic-movement` | 1860 | `ClassicMovement`, `ClassicMovementServer` |
| `quest-experience-multiplier` | 2766 | `QuestExpMultiplier` |
| `remove-single-raid-quest-requirements` | 2892 | `acidphantasm-removesingleraidquestrequirement` |
| `presettrader` | 2966 | `PresetTrader` |
| `quicksellflea` | 2407 | `QuickSellFlea` |
| `zezinhos-shopping-list` | 3242 | `ShoppingList` (server + client) |

REMOVED (9 — pinned before, not installed on the live server):

| slug | Forge id | why |
| --- | --- | --- |
| `autodeposit` | 1469 | no `AutoDeposit` folder on the live server |
| `cabinet-looter` | 3103 | no `CabinetLooter` folder on the live server |
| `handsarenotbusy` | 1298 | not installed on the live server |
| `bigbrain` | 902 | no `DrakiaXYZ-BigBrain` folder on the live server (see limits) |
| `auto-shoulder-swap` | 2639 | not installed on the live server |
| `quest-marker` | 2954 | live has `QuestGlow` (id 3214) instead; `quest-marker` is a different mod |
| `universal-co-op-exfil` | 2971 | not installed on the live server |
| `dehazardifier-updated-by-tetris` | 2125 | not installed on the live server |
| `fast-sell-in-flea` | 2207 | live has `QuickSellFlea` — that is id 2407 (Lacyway), not Kat's Fast Sell In Flea |

VERSION BUMPS under the version rule (nothing else changed version):

| slug | old | new | constraint |
| --- | --- | --- | --- |
| `manimals-icebreaker-backport` | 1.1.3 | 1.2.4 | `~4.1.6` |
| `manimals-icebreaker-backport-fika-sync` (addon) | 1.1.0 | 1.2.4 | addon: `mod_version_constraint 1.2.4` |
| `morebotsapi` | 2.1.1 | 2.1.2 | `~4.1.5` |
| `manimals-cs-gas` | 2.0.0 | 2.0.1 | `~4.1.6` |
| `manimals-10-interchange-expansion-backport` | 1.0.5 | 1.0.8 | `~4.1.6` |
| `weboverlay` | 1.11.0 | 1.12.0 | `^4.0.0` |

## Live name → Forge id

`live-set.json` holds `server_mods`, `client_plugins` and the `mapping` used by
`refresh.ps1 -LiveSet`. Folder names are **not** Forge slugs, so every non-obvious one was confirmed
from Forge metadata or the release archive name:

| live name | id | evidence |
| --- | --- | --- |
| `CustomMapLoader` | 3193 | `Tushonka Mapping SDK` teaser: "also includes a custom loader"; release asset is `CustomMapLoader.zip` |
| `DrakiaXYZ-GildedKeyStorage` | 865 | asset `DrakiaXYZ-GildedKeyStorage-2.1.1.7z`, guid `xyz.drakia.gildedkeystorage` |
| `tarkin-ladders` | 2649 | asset `tarkin-ladders-v1.0.4.zip`, owner `tarkin` |
| `betterkeys` | 1888 | asset `betterkeys-v1.8.0.zip` |
| `Interface10` | 3179 | description: "`BepInEx/plugins/Interface10` … `SPT_Runtime/user/mods/Interface10`" |
| `ShoppingList` | 3242 | asset `ShoppingList-1.2.4.zip`; "adds a server part and a client part; both are needed" |
| `QuickSellFlea` | 2407 | repo `Lacyway/QuickSellFlea` |
| `HollywoodFX`, `HollywoodGraphics` | 2003 | one archive, two client plugins — description names both |
| `MoreBotsPlugin`, `MoreBotsServer` | 2426 | repo tree has `MoreBotsPlugin.csproj` and `Server/MoreBotsServer.csproj`; Forge has no separate "MoreBots" mod |
| `Anvil-WebOverlay` | 2879 | owner `Anvil` |
| `LennoxP90-MapVariants` | 3040 | owner `LennoxP90` |
| `mpstark-dynamicmaps` | 1431 | owner `mpstark` |
| `DrakiaXYZ-Waypoints` | 827 | owner `DrakiaXYZ` |
| `ozen.ContinuousLoadAmmo` | 2112 | owner `ozen`, slug `continuous-load-ammo` |
| `Terkoiz.Skipper` | 1343 | owner `Terkoiz`, slug `skipper` |
| `Tyfon.UIFixes.Server` | 1342 | owner `Tyfon`, slug `ui-fixes` |
| `BlackDiv`, `BlackDivServer` | 2511 | guid `com.blackdiv.tacticaltoaster`, asset `WTT-BlackDiv-1.3.1.7z` |
| `AmmoStats` | 167 | asset `RaiRaiTheRaichu-AmmoStats-4.1.0.zip` |
| `Solarint-SAIN-ServerMod`, `SAIN` | 791 | SAIN ships the server mod |
| `WTT-ServerCommonLib`, `WTT-ClientCommonLib` | 2310 | WTT - CommonLib is the shared library for both halves |
| `[SVM] Server Value Modifier` | 236 | slug `server-value-modifier-svm` |

## Engine-provided and unmapped live names

Not Forge mods — do **not** invent ids for them:

| live name | why it is not a pin |
| --- | --- |
| `fika-server` | the installer downloads Fika itself (`$fikaServer`, Fika-Server-CSharp release) |
| `Fika` | ditto for the plugin (`$fikaPlugin`, Fika-Plugin release) |
| `spt` | the SPT server itself (`$spt7z`, SP-Tushonka 4.1.6 build) |
| `SPT-AKI Profile Editor.ModHelper` | helper folder dropped by the SPT-AKI Profile Editor tool, not by a mod archive |

Everything else resolved. Current coverage: **79 live names → 75 mapped → 51 Forge entries; 4 engine-provided; 0 unmapped.**

## Version-selection rule (why the pins look "random")

A version is eligible when its `spt_version_constraint` accepts SPT **4.1.6**
(`setup-spt.ps1`: targetEft `0.16.9.5.40743`, targetBuild `40743`). Among eligible versions the
**highest semantic version wins**. Never `published_at` — newest-published is routinely an older-track
build (`~4.0.x` pin for a 3.11 codebase). The constraint grammar handled by `refresh.ps1`:
`4.1.6`, `4.1.x`, `4.1`, `~4.1`, `~4.1.6`, `^4.0.0`, `>=4.1.3`, `>=4.0.0 <4.2.0`, `||` alternatives.
Tilde/caret follow npm semantics (`~4.1.3` = `>=4.1.3 <4.2.0`, `~4` = `>=4.0.0 <5.0.0`).

Forge **addons** have no `spt_version_constraint` at all — they carry `mod_version_constraint`
(parent mod version) instead. An addon row is therefore pinned to its highest semantic version and the
report calls it `OK-UNCONSTRAINED`. Today that is `manimals-icebreaker-backport-fika-sync` 1.2.4, which
matches its parent `manimals-icebreaker-backport` 1.2.4.

## Commands

```pwsh
# dry run: re-query Forge, print pinned vs highest-eligible drift, write nothing
pwsh -NoProfile -File manifest/refresh.ps1

# rewrite manifest/mods.json only
pwsh -NoProfile -File manifest/refresh.ps1 -Write

# rewrite mods.json AND the pin blocks in setup-spt.ps1 + install-mods.ps1 (idempotent)
pwsh -NoProfile -File manifest/refresh.ps1 -Apply

# resolve a live-set JSON through its mapping and diff against the current pack
pwsh -NoProfile -File manifest/refresh.ps1 -LiveSet manifest/live-set.json
```

On this Linux host: `/tmp/opencode/pwsh/pwsh -NoProfile -File manifest/refresh.ps1 …` (PowerShell 7).
Useful extras: `-RepoRoot <dir>` / `-ManifestPath <file>` to point at copies, `-CacheDir <dir>`,
`-NoCache`, `-SptTarget 4.1.x`.

Exit codes: `0` success (drift warnings included), `1` a row failed validation (not 4 fields, non-integer
id, x.y.z version, unknown/invalid kind), a Forge id that does not resolve, a resolved id whose slug
differs from the manifest, or (in `-LiveSet` mode) a live name with no mapping entry.

`-Apply` never runs blind: it re-validates every row against Forge first, then rewrites the
`# >>> GENERATED-MANIFEST` … `# <<< GENERATED-MANIFEST` block. `install-mods.ps1` gets those markers
inserted on first use (2-space indented rows, `# id|kind|slug|version` comment kept above the block).
Running `-Apply` twice produces no diff.

## Forge endpoints used

| endpoint | use |
| --- | --- |
| `GET /api/v0/mod/{id}` · `/api/v0/addon/{id}` | slug + name check for every pinned row |
| `GET /api/v0/mod/{id}/versions?per_page=50&page=N` · `/addon/{id}/versions` | version list with `spt_version_constraint`, `mod_version_constraint`, `link` |
| `GET /api/v0/mods?query=<text>&per_page=N` | name/slug discovery while building the mapping (`search=`, `/search` do not exist) |

All calls are unauthenticated, one every ~200 ms, and every response is cached on disk under
`manifest/.cache/` so re-runs do not re-hit the API. Cache files are raw JSON, gitignored, safe to delete.

## Honest limits

- The live set is the **folder names Matt read off `quadinaros-02` on 2026-11-11**, not a fresh scan of
  archive contents. A folder name never tells you a version, so versions come from the Forge rule above,
  not from the server.
- **`unitytoolkit` 2.0.2 is pinned without a matching constraint.** Its only 4.x version declares
  `spt_version_constraint: 4.1.5` (exact), which strictly does not accept 4.1.6. It is on the live server
  and is a client-side library, so the pin stays and `refresh.ps1` reports `KEPT-PINNED` instead of
  guessing. If Forge ever publishes a 4.1.6 build, `-Apply` will pick it up automatically.
- **`ExpandedTaskText` is ambiguous on Forge**: id 2153 (robpneu, `ExpandedTaskText.v2.1.0.zip`) and
  id 2389 (CJ's C# port, `ExpandedTaskText-2.0.3.7z`) both install a folder named `ExpandedTaskText`.
  The pack keeps 2153 — the original and the row the previous list already carried.
- **`bigbrain` (902) sits in the removed list** although SAIN/Waypoints/ABPS historically depended on it.
  The live client plugin list has no `DrakiaXYZ-BigBrain` folder, so it is not pinned; if a client crash
  turns out to be a missing BigBrain, that is the first thing to re-add.
- **`manimals-icebreaker-backport-fika-sync` (addon 98)** unpacks to
  `BepInEx/plugins/ManimalIcebreaker/ManimalIcebreakerFika.dll`, i.e. *inside* the parent mod's folder, so
  it can never appear in a top-level folder listing. It is kept (this is a Fika co-op pack and 1.2.4 is
  the Fika-2.4.6 sync build) and `-LiveSet` reports it as "pinned but not live" — expected, not a bug.
- Pins were chosen from Forge metadata. They are not byte-verified against the archives on the live
  server; only the URLs were checked (all 52 resolve).

Generated 2026-11-11 by the workstream that reconciled the pack; `refresh.ps1` is the source of truth
from here on.
