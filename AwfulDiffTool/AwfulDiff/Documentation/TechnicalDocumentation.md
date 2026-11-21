# Technical Documentation — AwfulDiff

This document is for developers who will read, modify, or extend the code. It describes the architecture, the major data and control flows, the reasoning behind the design decisions, and a candid list of known defects, limitations, and opportunities for future work.

Companion documents in this folder:

- **QuickStart.md** — build requirements, build/packaging instructions, smoke test.
- **UserManual.md** — end-user guide: every window, control, mode, and the command-line/VCS integration.

> **Naming note:** the repository directory retains a legacy name that predates the current product name (renaming it was not deemed worth churning the repo). The product, executable, window titles, namespaces, and settings location are all **AwfulDiff**, and nothing in the code references the old name. Ignore the folder name.

---

## 1. Overview

AwfulDiff is a Windows desktop tool for comparing and merging files:

- **Two-way diff** of text files (with an inline char-level view of changed regions) or binary files (a true byte-level hex diff), in three layouts: side-by-side, top/bottom, and a git-style single "unified" panel.
- **Three-way merge** with per-conflict resolutions, local ("File C") edits, snapshot-based undo/redo, and a strict exit-code contract for VCS integration (Perforce `p4merge`, git `mergetool`, SourceTree).
- **Directory diff** — recursive folder comparison with a filterable tree.
- **Sync points** — user-pinned anchor pairs that partition the diff into independently aligned segments, in both text and hex mode.

The design goals that shape everything below:

1. **Large files must stay smooth.** The main viewer is a single custom-drawn control, not a `ListView`/`TextBlock` per line. 100k-line files scroll at full frame rate because only visible rows are ever painted and formatted text is LRU-cached.
2. **The algorithmic core must be WPF-free.** Every diff/merge engine lives in `Services/` with no `System.Windows` references, so it can run on a background thread today and be unit-tested tomorrow without a UI host.
3. **One row model serves both text and hex.** A single `DiffLine` type is the currency of the whole app — text lines, gap fillers, and 16-slot hex rows are all instances of it. This makes layout switching, scroll syncing, and search uniform across modes.
4. **Be a good citizen for VCS tools.** Base-first argument order matching `p4merge`/Meld, an exit code that means "the merged result reached disk", and help text that documents the contract.

---

## 2. Technology Stack & Project Layout

| Fact | Detail |
|---|---|
| Framework | .NET 9 (`net9.0-windows`), WPF |
| Project | Single project `AwfulDiff.csproj`, no solution file, **zero NuGet dependencies** |
| WinForms | Referenced (`UseWindowsForms`) solely for `FolderBrowserDialog`; the injected `System.Windows.Forms` / `System.Drawing` global usings are removed via `<Using Remove=...>` to avoid WPF type collisions — the dialog is fully qualified at its single call site |
| Language features | Nullable reference types + implicit usings enabled |
| Manifest | `app.manifest`: `asInvoker`, Per-Monitor V2 DPI awareness |
| Packaging | Framework-dependent or single-file self-contained `dotnet publish` (see QuickStart.md §4); no installer, no `.pubxml` profiles |

```
AwfulDiff.csproj / app.manifest
App.xaml(.cs)                  startup, CLI routing, exit-code contract
MainWindow.xaml(.cs)           two-way diff window (3 layouts × text/hex)
ThreeWayMergeWindow.xaml(.cs)  three-way merge window
DirectoryDiffsWindow.xaml(.cs) recursive folder compare
SettingsDialog / MergeSetupDialog / ColorPickerDialog / CrcDialog / StructPatternDialog   (.xaml + .cs)

Models/                        POCOs — no logic, no UI
  AppSettings.cs               settings POCO + JSON (NaN↔null converter), theme overrides, struct patterns
  DiffAlgorithm.cs             enum { Standard, Patience }
  DiffLine.cs                  the dual-purpose row (text line AND 16-slot hex row)
  FileModel.cs                 path, lines, raw bytes, mtime; binary sniffing
  LayoutMode.cs                SideBySide / TopBottom / Unified
  HexPrimitives.cs             the inspector's primitive type table (name + size), shared lookup
  HexStructPattern.cs          named struct pattern: ordered fields (type × count) + endianness

Services/                      pure logic — WPF-free except the theme pair
  DiffEngine.cs                text diff: Myers + Patience, sync points, inline regions, unified builder
  ByteDiff.cs                  byte-level hex diff: Myers + resync + slot/mask rowizer
  CharDiff.cs                  char-level changed-region computation (bounded LCS)
  MergeEngine.cs               three-way merge: classification, resolution, display rebuild
  MergeUndoStack.cs            snapshot undo/redo with edit coalescing
  DirectoryDiff.cs             recursive folder compare + tree filter (safe off-thread)
  FileService.cs               BOM-aware load, mtime-keyed file cache
  HexFormat.cs                 single authority for byte→column math
  HexValueCodec.cs             endianness-aware decode/encode for inspector primitives (WPF-free)
  StructuralDiff.cs            advisory structural analysis (C-family/Rust/Python/JSON/XML)
  SyntaxHighlighter.cs         kind-based tokenizer
  ThemeManager.cs              app-wide theme/font/syntax state + resource dictionary swap*
  DiffViewerTheme.cs           flat brush bundle per theme; single Create() factory*
  SettingsService.cs           %APPDATA%\AwfulDiff\settings.json load/save
  CommandLineParser.cs         argument parsing (never throws)
  ClipboardHelper.cs           non-blocking OLE clipboard write (Win32 interop)
  Crc32.cs                     CRC-32 for hex selections
  ByteEdit.cs                  byte-edit helpers

ViewModels/                    orchestration, caching, INotifyPropertyChanged
  MainWindowViewModel.cs       file state, diff orchestration, sync points, edits
  MergeWindowViewModel.cs      merge session state, undo/redo, save/close contract
  DirectoryDiffsViewModel.cs   compare orchestration, filtering, recent pairs

Controls/
  FastDiffViewer.cs            the custom-drawn viewer (~3,200 lines) — all panes, text + hex
  HexInspector.xaml(.cs)       value reinterpretation panel (primitives + custom structs); right-side byte editor
  DiffToolbar.xaml(.cs)        toolbar (bindings only; small code-behind for Ctrl-state)

Resources/
  Themes.xaml                  "Awful" theme (chrome palette + styles)
  Themes.SystemDark.xaml       VS Code dark palette (same key set)
  Themes.SystemLight.xaml      VS Code light palette (same key set)
  Controls.Themed.xaml         theme-agnostic scrollbar/checkbox chrome, merged after the active theme

Converters: none custom — only the built-in BooleanToVisibilityConverter, instantiated inline in XAML.
```

\* `ThemeManager`/`DiffViewerTheme` are the only Services that touch WPF (they own brushes and `Application.Resources`). Everything else in the layer is directly unit-testable.

---

## 3. Architecture

### 3.1 Layering

```
┌──────────────────────────────────────────────────────────────────┐
│  Windows & Controls — rendering + input                          │
│  MainWindow · ThreeWayMergeWindow · DirectoryDiffsWindow         │
│  dialogs · FastDiffViewer · HexInspector · DiffToolbar           │
├──────────────────────────────────────────────────────────────────┤
│  ViewModels — orchestration, result caching, INotifyPropertyChanged│
│  MainWindowViewModel · MergeWindowViewModel · DirectoryDiffsVM   │
├──────────────────────────────────────────────────────────────────┤
│  Services — pure logic (WPF-free except ThemeManager/            │
│  DiffViewerTheme): DiffEngine · ByteDiff · CharDiff ·            │
│  MergeEngine · MergeUndoStack · DirectoryDiff · FileService ·    │
│  HexFormat · StructuralDiff · SyntaxHighlighter ·                │
│  SettingsService · CommandLineParser · ClipboardHelper · Crc32   │
├──────────────────────────────────────────────────────────────────┤
│  Models — POCOs: DiffLine · FileModel · AppSettings ·            │
│  LayoutMode · DiffAlgorithm                                      │
└──────────────────────────────────────────────────────────────────┘
```

Dependency rule: arrows point down only. `Services` never references `ViewModels` or Windows; the engines take `FileModel`s (or raw byte arrays) and return row lists.

The split is **MVVM in spirit, pragmatic in practice**. Window code-behind deliberately owns chrome and geometry concerns — title-bar drag/restore, maximize behavior, scroll-anchor preservation across layout switches, editor visibility, search bar positioning — because those are presentation mechanics that would bloat the view models with no testable logic. View models never touch `System.Windows` types except where a command or event must marshal to the UI thread (`Dispatcher.BeginInvoke`).

### 3.2 The custom-drawn viewer (and why)

`FastDiffViewer` is one `Control` per pane with **zero visual children**: everything — text, gutters, accent bars, gap stripes, selection, the 4px diff-map overview bar, and even the scrollbars — is painted in `OnRender(DrawingContext)`.

Why not a virtualized `ItemsControl`? Because the hex view needs per-*byte* rendering inside a row (16 slots with independent colors and hole-stripping), and because per-line WPF elements plus their bindings don't scale to 100k+ rows at 20px line height. A single element gives:

- draw only what's visible (no virtualization plumbing needed),
- full control of the hex slot model, gap stripes, and diff map,
- monospace layout by arithmetic: the character advance is measured once from a single "X" glyph (Consolas is assumed monospace), so column → pixel and pixel → column are integer math.

The cost is that hit-testing, selection, search navigation, and scrollbars are all hand-rolled. That concentration is the main maintenance hotspot (see §9).

Key layout constants: line height 20px, title bar 30px, text gutter 56px / hex gutter 84px, accent bar 3px, diff map 4px, scrollbars 14px.

### 3.3 One row model for text and hex

`DiffLine` is the single currency of the app. In text mode it carries line content, original line number, status (`Unchanged`/`Added`/`Removed`/`Modified`/`Gap`), char-level changed regions (for inline highlighting), an `OtherSideAnchor` (how many non-gap lines precede this row on the *other* pane — used by the "≈N" anchor chips and anchor jumps), and structural-analysis notes.

In hex mode the same type carries a 16-slot model:

- `ByteStart` — file offset of the first occupied byte in the row; **`null` marks a gap filler** (a row that exists only to align the two panes).
- `HexSlotMask` — which of the 16 slots are occupied.
- `HexAddedSlots` / `HexRemovedSlots` / `HexModifiedSlots` — per-slot role masks.
- `HexGapSlots` = opposite mask & ~own mask — the holes where the *other* side has bytes this one doesn't.

**Invariant:** the byte at slot `c` is `ByteStart + popcount(HexSlotMask & ((1 << c) - 1))`. All hex rendering, selection, editing, and sync-point resolution derive from this; it is what makes the two row views (aligned-with-holes vs fixed-address) repackable from one diff pass (§5.2).

### 3.4 Theme system: two parallel channels

There are **two independent colour channels**, and they only partially overlap:

1. **XAML chrome** — window frames, toolbars, dialogs, tree views. Coloured by three resource dictionaries (`Themes.xaml`, `Themes.SystemDark.xaml`, `Themes.SystemLight.xaml` — same key sets, different palettes) referenced via `DynamicResource`. `ThemeManager.ApplyUiResources` clears and re-adds the merged dictionaries: the active theme dictionary **always followed by** `Controls.Themed.xaml` (theme-agnostic scrollbar/checkbox chrome that must survive the swap).
2. **Viewer brushes** — everything `FastDiffViewer` paints cannot use XAML resources inside `OnRender`. It uses a shared `DiffViewerTheme`: a flat bag of ~60 brushes/pens/fonts rebuilt by the static `DiffViewerTheme.Create(name, overrides)` factory and pushed to all live viewer instances (tracked via a static list of weak references).

Per-theme **custom colour overrides** (Settings → six diff roles) flow *only* into channel 2: `ApplyOverrides` recolors whole brush families per enabled role (foreground + accent + diff-map marker = chosen colour; background = a translucent `0x30` tint of it). Hex parsing is strict (`#RRGGBB`/`#AARRGGBB`, leading `#` optional, named colours rejected) so a typo can't poison the theme.

All XAML that can ever be re-themed must use `DynamicResource`; a stray `StaticResource` freezes that element at first-show colour. (All windows declare borderless chrome in XAML; `WindowStyle`/`AllowsTransparency` are immutable after show, so theme switching changes palette and font only — the custom title bar is always present.)

### 3.5 Settings & persistence

- Location: `%APPDATA%\AwfulDiff\settings.json`, camelCase indented JSON, via `System.Text.Json`.
- `AppSettings` holds window geometry (NaN = "not yet placed"), layout mode, per-mode toggles (text vs hex stashes for Sync/ShowGaps/Inline/Timestamp), theme name, font scale (clamped 0.5–2.0), the diff algorithm choice, custom colour overrides keyed by theme name, directory-diff preferences, recent directory pairs (cap 8), and merge-window geometry.
- A custom `JsonConverter<double>` maps **non-finite values ↔ JSON null** — JSON has no NaN, and without this every `Save()` threw and the file was never written.
- Load: missing/corrupt file → defaults. Save: catch-all silent ("persistence is non-critical"). Most values save at exit; a few toggles (merge "show merged output", directory-diff show-unchanged) persist immediately so they survive even an abrupt exit before the main window's save-on-close runs.

---

## 4. Core Data Structures

### 4.1 `DiffLine` (Models\DiffLine.cs)

Covered in §3.3 — status, text payload, changed regions, anchor, structural notes, and the hex slot masks with the popcount invariant. It is a mutable class (not a record) because rows are stamped in place after the diff (anchors, structural notes, resolution marks).

### 4.2 `FileModel` (Models\FileModel.cs)

Holds `FilePath`, decoded `Lines`, `RawBytes`, size/mtime. Two sniffing methods drive mode selection:

- `IsBinary()` — null byte within the first **8192 bytes**.
- `RequiresHexView() => IsBinary() && !IsKnownTextType(FilePath)` — known text/code extensions (the union of the open-dialog filter and the syntax highlighter's language table, plus common text formats) **always open in the text view** even if they embed null bytes (e.g. C sources with `\0`).

`FileService` loads files BOM-aware and keeps a static cache (cap 64, keyed by path ordinal-ignore-case): an unchanged `LastWriteTime` skips the disk read entirely. **Every load allocates a fresh byte array** — this is what makes reference identity a valid freshness test for the hex result cache (§6.2).

### 4.3 The merge model family (Services\MergeEngine.cs)

| Type | Role |
|---|---|
| `MergeResolution` | Per-slot user choice: `Unresolved`, `TakeA`, `TakeB`, `OrderAB`, `OrderBA`, `KeepBase` |
| `MergeItem` | One base coordinate: status/text/char-regions for A and B, insertion lines, classification, indexes into Changes/Conflicts |
| `MergeChange` | A non-conflict change unit (insertion or deletion); carries rejected-line sets for "reject line" toggling |
| `ConflictBlock` | A maximal run of conflicting items; owns per-slot resolutions and the slot-asymmetry flags (`(StatusA==Removed) != (StatusB==Removed)` → delete-vs-modify, which hides OrderAB/OrderBA). Maintains `StartRow`/`FirstResolvedRow` for navigation |
| `MergeState` | The session: base/A/B files, the fixed `Items`/`Changes`/`Conflicts` collections, the three row-aligned display triples (`TripleA/TripleBase/TripleB`), row↔item/slot maps, the saved snapshot (base lines joined), and the local-edit state (`ManualMergedOverride` + `ManualEditBaseline`) |

**The load-bearing invariant:** `Items`, `Changes`, and `Conflicts` are fixed after construction. All display rows, maps, and snapshots derive from them; resolutions are stored *by index* into those collections. That is what makes a 4-field undo snapshot (§5.3) sufficient to restore any prior state exactly.

`IsDirty => EffectiveContent != SavedSnapshot`, where `EffectiveContent = ManualMergedOverride ?? BuildMergedContent` and is **null while any conflict is unresolved** — the app refuses to produce output until the merge is complete.

---

## 5. Major Data Flows

### 5.1 Text diff pipeline (`DiffEngine`)

Entry: `DiffEngine.Compute/ComputeAsync(fileA, fileB, ignoreWhitespace, useTimestamps, reportProgress, syncPoints, computeInlineDiffs, algorithm)` → `(FileA, FileB, InterleavedA, InterleavedB)`.

1. **Canonical order.** The shorter input becomes "canonical first"; equal lengths break the tie by ordinal lexicographic comparison. `Compute(A,B)` is then an *exact mirror* of `Compute(B,A)` — transposed matches are remapped at the end. This symmetry is what makes Swap a cheap status flip (§6.2) and keeps every cached structure consistent across swap-flip.
2. **Hash-first equality.** A hash comparison (`Eq`, aggressively inlined) short-circuits identical content before any diff work.
3. **Prefix/suffix strip** of common lines shrinks the problem for the engine.
4. **Engine dispatch.** Patience when `n+m > 8` and within budget — the budget is `(N+M) × 16` line visits; otherwise straight Myers.
   - *PatienceStep:* anchor on lines unique in both subranges, longest-increasing-subsequence over B positions, recurse between anchors; falls back to Myers when the budget exhausts.
   - *MyersCore:* bidirectional middle-snake, O(N+D²), with depth-monotone forward/backward envelopes and an interval `TrySplit` (see §8). If no interior split is found → `FallbackLCS` → Hirschberg linear-space LCS.
5. **Interleaving / pairing.** `BuildInterleaved` pairs Added/Removed runs: `min(uA, uB)` lines become `Modified` with char-level changed regions computed by `CharDiff` in *both* directions (under ignore-whitespace, whitespace-only pairs are demoted to unchanged); the surplus becomes gap fillers on the opposite side.
6. **Anchor annotation.** `AnnotateAnchors` stamps `OtherSideAnchor` (count of other-side non-gap lines preceding) — valid because the concatenated interleaved lists stay row-aligned by construction.
7. **Unified builder** (`BuildUnified`) produces the git-style single-panel rows; text only, never applied to hex output.

**Sync points:** `ComputeWithSyncPoints` partitions both files into inclusive segments at the anchor pairs and runs a full-budget diff per segment (so one gnarly region can't starve the rest), offsetting line numbers afterwards. **Timestamp mode** treats the older file (by last-write time) as base: it swaps the input pair, computes, and swaps the results back — reclassification is purely via the swap, so no separate code path exists.

**Ignore whitespace (git `-w` style):** `CharDiff.StripAllWhitespace` feeds both the diff keys and the inline changed-region computation; regions are mapped back to original indices through an index array (`map[i]` = original index of the i-th non-whitespace char). Display text is never altered — only equality and region math. Trade-off: `foo bar` vs `foobar` compares equal.

**Structural analysis** (advisory) runs on the same background task immediately after the diff: language detected from extension, both files analyzed, findings (virtual/const/generics/lambdas/closures/type mismatches across the mapped line pairs) sorted and capped at 200, then stamped onto rows as gutter triangles + tooltips. Text view only; cleared in hex mode.

### 5.2 Hex (byte-level) diff pipeline (`ByteDiff`)

Entry: `ByteDiff.ComputeWithSyncPoints(a, b, anchors, computeInlineDiffs, cellBudget = MaxWorkCells(100_000_000), autoResync, reportProgress, cancellationToken)` → `(RowsA, RowsB, FallbackSegments)`.

1. **Per-segment byte Myers** with a work-cell budget (100M cells/segment). Sync-point anchors are *pinned*: `matchB[anchorA] = anchorB` is forced, unpairing whatever previously matched that B position. Anchors must be co-monotone — `IsAnchorSetOrdered` (public) is the validation the sync-point UI calls before accepting a hex anchor pair.
2. **Over-budget segments** are handled by `autoResync`: `BuildResyncSegment` recursively splits at the midpoint, capped at `MaxResyncLeaves = 1024`; leaves still over budget fall back to `BuildPositionalRows` (positional pairing, no Myers) and increment `FallbackSegments`, which is surfaced in the UI.
3. **Aligned rows** (`BuildAlignedRows`) use a 5-kind slot model: `KEmpty=0, KMatch=1, KX=2` (modified), `KDel=3, KIns=4`. A "shuffle" pass re-pairs net-balanced insertion/deletion excursions so an equal-length replacement renders as per-byte `Modified` (KX) instead of a delete+insert band; `RepairSpan` demotes bytes that are actually equal back to `KMatch`.
4. **Per-row masks** land on the `DiffLine` (§3.3). The popcount invariant holds for every occupied slot.
5. **Fixed-address rows** (`BuildFixedAddressRows`, "Show Gaps OFF") repack the *same* pairing into dense fixed-address rows using only `ByteStart + HexSlotMask` — **no second diff pass**. One engine run yields two row views: aligned-with-holes (gaps shown) and fixed-address (git-`--binary`-style dense).
6. **Progress & cancellation:** a `SegmentContext` accounts side-A bytes for an exact-percentage progress report (per-segment updates capped at 95%/99%, `Finish()` → 100%); the token is checked at segment boundaries and inside long loops.

Column layout has a single authority, `HexFormat`: 16 bytes/line, fixed 76-column layout, hex slot `c` at column `10 + c*3 + (c>=8 ? 1 : 0)`, ASCII at column `60 + c`. The viewer, the inspector, and changed-region math all derive from it.

### 5.3 Three-way merge (`MergeEngine`)

**Construction — `CreateMerge(base, a, b)`:**

1. Two text diffs: base→A and base→B (so each side's changes are expressed *against the base*).
2. Interleaved projection onto base coordinates (`LineProj`/`InsertProj`). If a projection count doesn't match, an `InvalidOperationException("Projection mismatch")` is thrown — a deliberate invariant guard rather than silent misalignment.
3. `ClassifyAndBuild`: per base line — nothing changed → `None`; one side modified → `ChangeA`/`ChangeB`; both removed → `DeleteAgreed`; both modified to equal text → `BothSame`; otherwise `ConflictLine`. Insertions: equal on both sides (via `SequenceEqual`) → `BothSame`, else an insertion conflict owning `max(InsA.Count, InsB.Count)` slots. Maximal conflict runs become `ConflictBlocks` with slot-asymmetry flags.

**Resolution & display:**

- The user resolves per slot (`TakeA`/`TakeB`/`OrderAB`/`OrderBA`/`KeepBase`). Order options are hidden on asymmetric slots (a delete-vs-modify can't be "ordered").
- `RebuildDisplay` regenerates the three row-aligned display triples and all row maps from the fixed model + resolutions. Navigation rows (`StartRow`, `FirstResolvedRow`) are maintained here.
- **`BuildConflictSequence` is shared by both `RebuildDisplay` and `BuildMergedContent`** — the on-screen preview is byte-for-byte what gets saved. OrderAB/OrderBA over same-direction runs emit all first-side lines then all second-side (whole block or selection).
- `KeepBase` needs no capture: it's derivable at build time — the original base line for a line conflict, nothing for an insertion conflict.

**Output:**

- `EffectiveContent = ManualMergedOverride ?? BuildMergedContent`; null while `UnresolvedCount > 0`.
- Rejected lines restore base text (rejecting a deletion un-deletes it).
- **Local edits ("File C"):** once everything is resolved, the merged output can be edited directly. That sets `ManualMergedOverride` + `ManualEditBaseline`. If a later refresh changes the computed merge, `RebaseManualEdits` performs a *nested* three-way merge (baseline, edited text, fresh result) resolving every sub-conflict `TakeA` — i.e. **a colliding edit is never clobbered**. It's a no-op while anything is unresolved or when fresh == baseline.

**Undo/redo (`MergeUndoStack`):**

- A `MergeSnapshot` captures exactly four things: per-block resolution arrays (cloned), per-change rejected-line sets, the `ManualMergedOverride` reference, and the `ManualEditBaseline`. Everything else re-derives via `RebuildDisplay` — which is only possible because of the fixed-model invariant (§4.3).
- Cap 100 entries; **edit coalescing** — an edit pushed within 1500 ms of the last edit just refreshes the top entry's timestamp ("one typing pause is one undo step"); redo entries are re-pushed as non-edits.

**Save:** UTF-8, no BOM, to `_outputPath ?? BaseFile.FilePath`; sets `SavedSnapshot` (clearing `IsDirty`) and `WroteMergedOutput` — the flag that drives the CLI exit code (§6.1).

### 5.4 Directory comparison (`DirectoryDiff`)

WPF-free; runs on a background thread.

1. **Walk:** recursive compare with a visited `HashSet<string>` (ordinal-ignore-case) guarding junction/symlink loops. An unreadable directory becomes a warning and is treated as empty.
2. **Merge:** union of entry names — left spelling preferred, directories first, then ordinal-ignore-case sort. A kind mismatch (directory vs file at the same name) emits *both* sides one-sided.
3. **Content compare:** length + mtime equal → `Identical` with **no disk read**; otherwise `ReadAllBytes` + `SequenceEqual`. IO errors (`IOException`/`UnauthorizedAccessException`/`FileNotFoundException`) become warnings — "read failed (…) — treated as different". Deliberately *not* routed through `FileService` (it would pollute that cache), and there is no content hashing anywhere in this path.
4. **One-sided directories are leaves** ("v1" behaviour — their contents aren't enumerated).
5. **Filtering** (`FilterTree`, post-order): a file is visible when *show unchanged files* or its status isn't `Identical`; a folder when *show unchanged folders*, it's one-sided, or it has visible children. `DirEntry.VisibleChildren` is **reassigned, not refilled**, so the `HierarchicalDataTemplate` rebuilds containers cleanly; `IsExpanded` lives on the model (default true — a fresh compare opens fully expanded). Toggling filters re-filters the cached full tree with zero disk I/O.

---

## 6. Major Control Flows

### 6.1 Startup, CLI routing, exit-code contract (`App.xaml.cs`)

- `CommandLineParser` (never throws): a flag is `-x` or any token of length > 1 starting with `-`; `-h`/`--help`; `-o`/`--out`/`--output` consumes the next token *only if it doesn't start with `-`*; unknown flags are ignored; up to 3 positional file arguments, and a **4th positional becomes the output path**. The usage text documents base-first order ("matches Perforce p4merge and Meld").
- Routing in `OnStartup`: help → print usage + `Shutdown(0)`; **≥3 files → merge window** (three `FileService.LoadFile` calls; any failure → "Load Error" message box + `Shutdown(1)`); otherwise the main window with 0–2 files preloaded.
- There is no `StartupUri`: `Application.MainWindow` is assigned explicitly, because "Start Merge" from the main window closes that window and shows the merge window instead (the `OnLastWindowClose` semantics would otherwise shut the app down).
- The merge window's `Closed` handler calls `EnsureMergedOutputWritten()`, and `OnExit` sets `Environment.ExitCode = 1` **iff** a CLI merge ran and `WroteMergedOutput` is false. In words: *exit 0 only when the merged result reached disk* (git `trustExitCode`; Perforce's fallback behaviour). Diff mode and in-app merges always exit 0.
- Note: `AttachConsole(ATTACH_PARENT_PROCESS)` is called unconditionally at the top of `OnStartup` under a "DEBUG TESTING" banner — it runs in Release builds too (see §9).

### 6.2 Diff computation & caching in the main window

- **Text:** fire-and-forget `_ = Task.Run(() => DiffEngine.Compute(...))`. The text engine has **no cancellation token** (hex does). Progress is marshaled via `Dispatcher.BeginInvoke`; a 500 ms timer gates the "computing" overlay so fast diffs never flash it.
- **Hex:** `Task.Run` with a real `CancellationTokenSource`; a Cancel button appears after 1 s (hex only, matching engine capability); the fixed-address repack runs on the same background thread before results are marshaled back.
- **Result caches in the view model:** `_cachedDiff / _cachedInterleaved / _cachedUnified / _cachedFindings`, fingerprinted by (ignoreWhitespace, useTimestamps, showInlineDiffs, algorithm) plus a "is hex" flag; and a `_hexCache` tuple (aligned + fixed row lists, inline flag, per-byte flag, swap direction, fallback count). Hex cache validity is tested by **`ReferenceEquals` on the `RawBytes` arrays** — because `FileService` allocates a fresh array per load and every edit produces a fresh array, reference identity *is* the freshness test, with no hashing.
- **"Apply change" operations** recompute synchronously against small synthetic models (cheap enough to stay on the UI thread).
- **Swap** takes an instant path when the text cache is valid and there's no unsaved working copy: `FlipDiffListStatuses` (Removed ↔ Added) + rebuild of the cached unified rows — O(N), no recompute. This only works because of the canonical-order mirror symmetry (§5.1).

### 6.3 Text ↔ hex mode switching; layout switching

The `IsHexView` setter is a pipeline, and order matters:

1. **Layout fix** — hex forbids the Unified layout (it snaps back to SideBySide; `SetLayout` is a no-op in hex).
2. **Materialize the working copy** — if the right pane has unsaved text edits they're UTF-8 encoded/decoded into bytes so they survive the switch (invariant: at most one of `_rightWorkingLines` / `_rightWorkingBytes` is non-null at any time).
3. **Switch mode toggles** — four settings (sync scroll, show gaps, inline diffs, timestamp mode) are stashed per text-vs-hex and restored from the other stash; they persist independently.
4. Propagate to all five viewers (Left/Right for side-by-side, Top/Bottom, Unified), then recompute.

**Layout switching** (side-by-side / top-bottom / unified) preserves position via *canonical* scroll anchors: the current row is mapped through pane/unified index mappings (identity when gaps are shown or no cache exists; a Modified row expands to two rows in unified space), the grids toggle, and `SetScrollAnchor` runs at `DispatcherPriority.Loaded`. The Unified pane is deliberately **not** sync-bound — it's a single panel.

**Editing:** text edits happen in a `TextBox` overlay with a 300 ms debounce timer → flush to working copy → cache invalidation (working copy preserved) → recompute. Hex edits come from the pane-header byte-entry box (input is stripped of whitespace/colons/dots, uppercased, must be even-length ≤ 8192 chars; replaces the selection or appends at EOF) and the hex inspector's `RightEditRequested(startByte, removeCount, insertBytes)` which splices `_rightWorkingBytes`. After a re-diff, the view model fires a "restore byte selection" event so the edited bytes stay selected.

**Hex inspector struct patterns:** beyond its primitives (int8…double, char), the inspector can interpret a selection as an array of instances of a user-defined **struct pattern** (`Models\HexStructPattern.cs`): an ordered list of fields, each a primitive repeated N times, with a per-pattern byte order. All value decode/encode runs through `Services\HexValueCodec.cs` (a WPF-free, endianness-aware port of the old inline `BitConverter` logic — little-endian output is byte-identical to before). Patterns are persisted in `AppSettings.HexStructPatterns` and managed by `StructPatternDialog` (the "⚙ structs…" button); Save/Delete persist immediately via the settings load→mutate→save idiom. Selecting a pattern feeds its `TotalSize` into the existing `TypeSizeChanged` → viewer selection-snapping flow, so clicks snap to whole instances; the editable right panel commits **per field** through the same `RightEditRequested` splice path.

### 6.4 Sync points (interaction + semantics)

Two-step, cross-pane: clicking a line starts a pending state (a "◀ PENDING" marker on that row); a single click in the **opposite** pane confirms the pair.

- **Text:** `AddSyncPoint` validates 1-based bounds and requires the two lines to have **identical content** (otherwise a message box explains why).
- **Hex:** `AddHexSyncPoint` resolves the clicked slot to a byte (`ByteStart + slot ordinal` when occupied) and rejects pairs that would make the anchor set non-co-monotone via `IsAnchorSetOrdered`.
- Display line numbers for existing anchors are kept current by rescan passes; applying insert/remove changes to the working copy auto-adjusts each sync point's second-side line number so the pairing stays meaningful.

### 6.5 Merge-window operations

- **Navigation:** F3/Shift+F3 walk conflicts; Ctrl+Up/Down walk non-conflict change units — both over display-ordered collections with modulo wrap, both funneling into one handler that scrolls all three panes to the row (centered).
- **Scroll sync** is a raw-ratio pass-through: "the triples are equal length, so scroll sync is a raw ratio pass-through" — no mapping needed.
- **Right-click menu** is built per status (conflict slot vs change line), is selection-aware, dedupes (block, slot) targets, and offers OrderAB/OrderBA only when no target slot is asymmetric.
- **Merged-output editing:** enabled only when `UnresolvedCount == 0`; enabling fills the editor and sets the edit baseline. The editor normalizes CRLF/CR → LF; non-programmatic changes push an *edit* undo. Inside the editor, the `TextBox`'s native character-level undo wins for Ctrl+Z; merge-operation undo/redo remains available via toolbar buttons (window-level handling applies outside the editor).
- **Close contract:** unresolved → Yes/No (stay / discard); dirty → Yes/No/Cancel; otherwise close. For CLI runs, `EnsureMergedOutputWritten` writes on close only when an explicit output path was given and the state is clean — it *refuses* to write over an explicitly discarded or still-dirty merge ("never override an explicit discard").
- **Base-pane editor visibility** is toggled via `Dispatcher.BeginInvoke` (so focus lands after layout), with the anchor line computed differently in merged-output mode (count non-gap, non-rejected rows) vs original-base mode (map through `MergedIndexOfBaseLine`).

### 6.6 Directory → main-window handoff

Double-clicking a file pair opens it in the main window: an existing `MainWindow` is found via `Application.Current.Windows.OfType<MainWindow>().FirstOrDefault()` (deliberately *not* `app.MainWindow`, which may be stale) — and if none exists, a **new `MainWindow` is constructed around the same view model**. WPF cannot re-`Show` a closed `Window`; recreating the window around the surviving VM carries all state across. Folder drag-and-drop: ≥2 folders → left+right in drop order; one folder + side hint → targeted; one folder, no hint → fills the empty side or replaces right.

---

## 7. Threading & Performance Model

**The UI thread never blocks on:** diff computation (`Task.Run`), the clipboard (probe + off-thread retries with a generation counter — see §8), directory compare (`Task.Run` + `ContinueWith`, faulted → "Comparison failed"), or settings save (synchronous but tiny; failures swallowed). Progress and results are marshaled back with `Dispatcher.BeginInvoke`; message boxes appear only where blocking is acceptable.

**Rendering performance levers:**

- `ScanDiffBlocks()` runs *inside* `OnRender` — collapsed viewers never scan at all.
- Content max-width is tracked lazily (960px seed); a wider line triggers one `InvalidateVisual` re-fire to grow the horizontal extent.
- Formatted text is LRU-cached: two instances (content 10,000 entries, gutter 4,096), built at 96 dpi with a 10,000px max width. The cache uses a self-referencing doubly-linked node list + dictionary index + sentinel for **O(1) promote/evict** (the original `LinkedList.Find()` was O(N) per promotion — see §8).
- Gap stripes are locked to content coordinates via a brush transform (`-hScroll, -scroll`) rather than per-frame geometry.

**Engine budgets (all deliberate):** Patience budget `(N+M)×16` line visits before falling back to Myers per subrange; `CharDiff` DP capped at 500,000 cells (beyond that, the whole line is marked changed); `ByteDiff` 100M work-cells per segment → resync split (≤1024 leaves) → positional fallback (count surfaced to the user); structural analysis capped at 100k lines / 200 findings. Swap is an O(N) status flip when the cache is valid.

---

## 8. Key Design Decisions & Hard-Won Pitfalls

These are the "why does the code look like this" notes. Several were discovered through real bugs; preserve them when refactoring.

**Diff engine**

1. **Myers work stacks must be passed `ref`.** A pass-by-value copy silently dropped all split frames and produced one giant `Modified` block instead of a proper diff.
2. **The middle-snake split region is an interval intersection:** `[max(0,k), fwdM[k]] ∩ [n−bwdM[kb], n−max(0,kb)]` with `kb = n−m−k`, using depth-monotone envelopes. Checking maximal runs instead gives false positives; checking the pre-snake range gives false negatives — both variants were tried and failed.
3. **Canonical argument order** (§5.1 step 1) is what makes Swap a status flip and keeps plain/gap per-line classification identical across swap-flip. Don't "simplify" away the mirror remapping.
4. **One diff pass, two hex row views.** The fixed-address rows are repacked from `ByteStart + HexSlotMask`; running a second engine for them would double cost and risk the two views disagreeing.
5. **`KeepBase` is derivable, not captured** — it emits the original base line (or nothing) at build time, so snapshots stay 4 fields.
6. **Preview == saved file** because `BuildConflictSequence` is shared by display rebuild and content build. If you add a resolution kind, implement it in *that one place*.
7. **Nested rebase resolves sub-conflicts `TakeA`** ("a colliding edit is never clobbered") — see §5.3.

**Rendering / UI**

8. **LRU via self-referencing node list** — `LinkedList.Find()` made every promotion O(N) and scrolling visibly stuttered on big files.
9. **Non-blocking clipboard:** the UI thread probes `GetOpenClipboardWindow` and retries *off-thread* with a generation counter. Synchronous retry on the UI thread was the "copy is extremely slow" bug (another process holding the open clipboard).
10. **Closed WPF windows can't be re-`Show`n** — hence recreating `MainWindow` around a surviving view model (§6.6).
11. **`DynamicResource` everywhere in themeable XAML**, and `Controls.Themed.xaml` is always merged *after* the active theme dictionary so scrollbar/checkbox chrome survives the swap.
12. **Hex cache validity by reference identity** — fresh `byte[]` per load/edit makes `ReferenceEquals` an exact freshness test with no hashing (§6.2).
13. **Title-bar drag-restore (maximized → follow cursor):** call `CaptureMouse()` *before* restoring geometry and reposition so the press point stays under the cursor; use the method, not the `Mouse.Capture` property. All three custom-chrome windows share this manual pattern.
14. **Restored window geometry:** `WindowStartupLocation = Manual` plus NaN-skip — XAML `CenterScreen` would clobber a restored position at show time.
15. **TextBox native undo wins inside the merge base editor** (character-level); merge-op undo is toolbar-driven there. Documented trade-off, not a bug.
16. **Gap rows only render with sync scroll on** (clamped at load) — gaps are meaningless without an aligned partner pane.
17. **`VisibleChildren` is reassigned, never refilled**, and `IsExpanded` binds TwoWay/PropertyChanged from the model — style-setter bindings don't fire the default LostFocus trigger, so expander clicks wouldn't sync back otherwise.
18. **Drop zones need `AllowDrop` on the inner TextBox too** — WPF only raises drag events over elements that accept drops; without it a drop on the path box never reaches the zone handler.
19. **The "bloom" glow is layered drawing, not a shader.** The "FILES ARE IDENTICAL" overlay's glow is multiple semi-transparent offset text draws in `OnRender`. There is no GPU/shader code anywhere — .NET 9 removed nearly the entire custom-shader surface (no `ShaderEffectFactory`, most `BitmapEffect`s gone), and an earlier shader-based visual-effects experiment was reverted. If you want real bloom, expect to go through `D3DCompile` + `PixelShader.SetStreamSource` with register-based constant binding — non-trivial work.

**Platform / persistence**

20. **JSON has no NaN:** the custom `DoubleConverter` maps non-finite ↔ null; without it every settings save threw and the file was never written.
21. **WinForms global usings are removed in the csproj** — `UseWindowsForms` injects `System.Windows.Forms` + `System.Drawing` usings that collide with WPF types project-wide. Keep them removed; fully qualify any new WinForms type.
22. **.NET 9 renamed `TickPlacement` members** (`Top`→`TopLeft`, `Bottom`→`BottomRight`). XAML using the old names crashes at runtime with a `FormatException`. (No current XAML uses ticks — but this bit the settings dialog during development; remember it if you add sliders.)
23. **Directory-diff mtime caveat:** length+mtime equality skips the content read; NTFS timestamps have coarse resolution, so a same-size rewrite within one tick reads as `Identical` (see §9).

---

## 9. Known Defects & Limitations

Candid list — things that are wrong, missing, or merely suboptimal in the current code:

1. **No automated test suite.** There is no test project in the repository, despite the algorithmic core (diff engines, merge engine, directory diff) being WPF-free and directly unit-testable. Correctness of the Myers split interval, the hex slot invariant, and the merge preview==saved guarantee currently rests on manual verification. This is the single biggest gap.
2. **Text diff is not cancellable.** `DiffEngine` has no `CancellationToken`; only the hex pipeline does (hence the Cancel button appears in hex mode only). A huge text diff must be waited out.
3. **Unconditional `AttachConsole` under a "DEBUG TESTING" banner** in `App.xaml.cs` — it runs in Release builds too. Harmless in practice, but it's debug scaffolding that should be `#if DEBUG`-guarded or removed.
4. **Stale `README.md`.** The root README still describes .NET 8 and an older feature set; the `Documentation/` folder (this file, QuickStart, User Manual) is current. Refresh or replace the README.
5. **Distribution size:** the single-file self-contained publish is ~163 MB (it embeds the runtime) plus a stray ~190 KB pdb in the publish folder. No ARM64 build is configured; trimming is off (correct for WPF, but the size is a real distribution cost).
6. **Settings persistence is best-effort:** most values save only at exit (a crash loses them), the write is non-atomic (no temp-file + replace), and save failures are swallowed silently. A few toggles persist immediately as a partial mitigation.
7. **Directory diff limitations:** one-sided directories are leaves (contents not enumerated); there's no content hashing — the length+mtime prefilter means a same-size rewrite within an NTFS mtime tick is reported `Identical`; and the case-insensitive name union collapses a pure case rename into a single entry (left spelling wins).
8. **`FastDiffViewer` is a ~3,200-line monolith** — rendering, selection, scrolling, hex slot logic, search navigation, and the diff map are intermingled in one file. It's the highest-risk file to modify and the natural candidate for a split.
9. **Structural analysis is advisory-only and heuristic** per language (gutter triangles + tooltips), capped at 200 findings / 100k lines; edge cases (e.g. C# arrow-body detection) are best-effort by construction.
10. **`CharDiff` inline regions give up past 500k DP cells** — very long similar lines fall back to whole-line highlighting instead of char-level regions.
11. **Search is case-insensitive substring only** (no regex, no word/whole-word mode, no hex-byte search), and selection is a single contiguous range per pane.
12. **No internationalization** — all strings are English, hardcoded in XAML and code.
13. **Pathological text-diff cost:** when Myers finds no interior middle-snake split, the fallback is Hirschberg linear-space LCS — O(N·M) time. Huge near-identical files with one giant divergent region can hit this.

---

## 10. Missing Features & Opportunities for Future Development

Ordered roughly by value-to-effort:

1. **A unit-test project targeting `Services/`.** The layer is WPF-free precisely so this is cheap. Prime targets: `DiffEngine` (Myers/Patience correctness, the split-interval LCS-optimality property, and *canonical-order mirror symmetry* — a fuzz target: for random file pairs, assert `Compute(A,B)` is the exact transpose of `Compute(B,A)`); `ByteDiff` (anchor pinning, resync stitching, the slot popcount invariant, fixed-address repack == aligned view minus holes); `MergeEngine` (classification, preview==saved byte equality, snapshot restore round-trips, rebase-never-clobbers); `CharDiff` region mapping; `DirectoryDiff` merge/filter.
2. **`CancellationToken` for the text engine** — parity with hex; the plumbing pattern already exists in `ByteDiff`.
3. **Directory diff v2:** enumerate one-sided directories (remove the "v1" leaf limitation), an optional content-hash mode (fixes the mtime-tick false negative), and a parallel directory walk.
4. **Atomic settings write** (temp file + `File.Replace`) and/or per-change persistence, so a crash can't lose or corrupt `settings.json`.
5. **Refresh `README.md`** to match the current feature set and .NET 9 (or make it a pointer at this folder).
6. **ARM64 publish target**, if the distribution audience warrants it.
7. **Search upgrades:** regex/word mode, hex byte search, and searching across both panes at once.
8. **File watching** (`FileSystemWatcher`) with an "file changed on disk — reload?" prompt; today external changes require a manual refresh (Ctrl+R).
9. **Split `FastDiffViewer`** into focused modules (rendering / interaction / hex slot logic / selection) behind its existing public surface.
10. **GUI export:** unified-patch or side-by-side report output from the main window — the CLI only writes files in merge mode; diff mode currently has no file output at all.
11. **i18n** via string resources if non-English support is ever needed.
12. **Adaptive Patience budget** — the fixed `16×(N+M)` line-visit budget is a heuristic; it could be tuned per file shape (e.g. more for highly repetitive files).

---

## 11. Developer Map — "I want to change X, where do I look?"

| Change | Start here |
|---|---|
| Colours (chrome) | `Resources\Themes*.xaml` (same key sets across the three themes) |
| Colours (viewer + per-role overrides) | `Services\DiffViewerTheme.cs` (`Create`/`ApplyOverrides`) + `Models\AppSettings.cs` override types |
| New toolbar button | `Controls\DiffToolbar.xaml` + a command in the relevant view model |
| New diff algorithm | `Services\DiffEngine.cs` — keep canonical order + mirror symmetry intact (§5.1, §8.3) |
| Hex row/column layout | `Services\HexFormat.cs` (column authority) + `ByteDiff` rowizer + the hex render path in `FastDiffViewer` |
| Merge behaviour/resolutions | `Services\MergeEngine.cs` (logic) + `ViewModels\MergeWindowViewModel.cs` (UI state, save/close contract) |
| Keyboard shortcuts | `OnKeyDown` in each window's code-behind |
| Settings add/change | `Models\AppSettings.cs` (field + JSON) + `Services\SettingsService.cs` + the window that saves on close |
| CLI/VCS contract | `Services\CommandLineParser.cs` + `App.xaml.cs` (routing, exit codes) + `MergeWindowViewModel.EnsureMergedOutputWritten` |
| New language: syntax / structure | `Services\SyntaxHighlighter.cs` / `Services\StructuralDiff.cs` (`LangSpec`) |
| Window chrome / title-bar drag | each window's code-behind (shared manual-capture pattern, §8.13) |
| Scroll sync behaviour | the nullable ratio DPs + events on `FastDiffViewer` (§6.5); tolerance guards prevent echo loops |

---

*Last updated: 2026-11-16. Verified against the source in this repository; where this document and the code disagree, the code wins — please fix the document.*
