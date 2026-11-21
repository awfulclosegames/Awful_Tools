# AwfulDiff — User Manual

AwfulDiff is a Windows desktop tool for comparing and merging files. It covers three jobs:

1. **2-way diff** — compare any two files, as text or as raw hex bytes.
2. **3-way merge** — resolve conflicts between a base file and two changed copies (the job VCS tools delegate to it).
3. **Directory compare** — recursively compare two folders and open any differing pair in the main window.

> The executable and window titles are **AwfulDiff**.

---

## Contents

1. [Main Window (2-way diff)](#1-main-window-2-way-diff)
2. [3-Way Merge Window](#2-3-way-merge-window)
3. [Directory Diff Window](#3-directory-diff-window)
4. [Settings](#4-settings)
5. [Command Line & VCS Integration](#5-command-line--vcs-integration)

---

## 1. Main Window (2-way diff)

Title bar: `AwfulDiff // Text File Comparator`. The custom title bar shows the change count (`| Δ N changes`), the two file names, and standard minimize / maximize / close buttons — you can also drag down from the top edge of a maximized window to restore it.

### 1.1 Loading files

Files get into the two panes in three ways:

- **Toolbar buttons** — `[◈] Left File...` (or `Ctrl+L`) and `[◈] Right File...` (or `Ctrl+Shift+R`) open a file picker with filters for text, source, and data files.
- **Drag and drop** — drop one or more files onto the window: two files go left/right (top/bottom), one file goes to the pane you hover over (the hovered pane glows cyan while dragging). Dropping **folders** opens the Directory Diff window instead.
- **Command line** — `AwfulDiff.exe left.txt right.txt` (see [Section 5](#5-command-line--vcs-integration)).

**Text vs hex, automatically.** When a file is loaded, AwfulDiff sniffs the first 8 KB for null bytes. A file that looks binary *and* has an unrecognized extension opens in **hex view**; files with known text/code extensions (`.cs`, `.json`, `.xml`, `.txt`, `.md`, …) always open as **text** even if they contain NULs. You can always flip `[Tx] Text` / `[Hx] Hex` by hand — a hex dump is generated for any file.

Loading new files clears any sync points and resets edit mode. Replacing a file that has unsaved edits prompts you to save first (same prompt when closing the window).

### 1.2 Toolbar controls

Left to right:

| Control | What it does |
|---|---|
| `[◈] Left File...` | Open a file into the left (top) pane — `Ctrl+L` |
| `[◈] Right File...` | Open a file into the right (bottom) pane — `Ctrl+Shift+R` |
| `[||] Side` | **Side-by-side** layout: two panes, one per file |
| `[=] Top` | **Top-bottom** layout: same two panes stacked vertically |
| `[≡] Single` | **Unified** layout: one git-style panel — `-` deletions, `+` additions (text only; entering hex snaps back to a two-pane layout) |
| `<-> Sync Scroll` | Keep both panes scrolled to the same proportional position (on by default) |
| `[+] Show Gaps` | Draw filler "gap" rows so both sides stay line-aligned. **Off** = compact view with anchor markers (see 1.6) |
| `▦ Per-Byte` | *(hex only)* Resynchronize alignment after each change block and shade only the individual bytes that differ, instead of whole amber rows |
| `<=> Swap` | Exchange left/right files — `Ctrl+S` |
| `~ Ignore Whitespace` | *(text only)* Compare as if all whitespace were stripped (git `-w` style) while still displaying the original text |
| `◫ Inline Diffs` | Highlight the differing regions *within* a modified line, not just the whole line |
| `▤ Structure` | *(text only)* Advisory structural analysis — gutter triangles for changed functions, signatures, types, lambdas, loops, braces, JSON keys, XML elements. Supports C/C++, C#, Java, Rust, Python, JSON, XML |
| `@ Timestamp Mode` | Reclassify: lines added to the *newer* file (by last-modified time) are shown as **Modified** instead of Added/Removed pairs — useful when one side is "the same file edited later" |
| `[Tx] Text` / `[Hx] Hex` | Switch view mode; the active button is highlighted |
| `[r] Refresh` | Re-read both files from disk and recompute — `Ctrl+R` |
| `⚙ Settings` | Open the Settings dialog (theme, font scale, colors) |
| `[▣] Directories...` | Open the Directory Diff window. **Tip:** Ctrl-click to compare the parent folders of the two currently open files |
| `[⋔] 3-Merge...` | Start a 3-way merge using the loaded files — `Ctrl+M` |
| `# N` + `Clear All` | Sync-point count and "clear all" (appears when sync points exist; two-pane layouts only) |
| `! Unsaved Changes ▾` | Appears when the right pane has unsaved edits: **Save Right**, **Save As...**, **Discard Changes** |

At the far right, a small legend shows the three diff colors: **Added** (green), **Removed** (red), **Modified** (amber).

### 1.3 Keyboard shortcuts

| Keys | Action |
|---|---|
| `Ctrl+L` | Open file into left pane |
| `Ctrl+Shift+R` | Open file into right pane |
| `Ctrl+R` | Refresh both files from disk |
| `Ctrl+S` | Swap files — *or* Save Right when the right editor (or hex view) has edit mode on |
| `Ctrl+M` | Start 3-way merge window |
| `Ctrl+F` | Show the search bar; type to find, `F3` / `Shift+F3` jump between matches |
| `Ctrl+Down` / `Ctrl+Up` | Jump to next / previous changed line |
| `Ctrl+C` | Copy the selection from the most recently used pane |
| `Delete` | *(hex edit mode)* Delete selected byte(s) |
| `F11` | Cycle layout: Side → Top → Single (Side ↔ Top only in hex view) |
| `Escape` | Close the search bar, or cancel a pending sync-point confirmation |

In hex view with edit mode on, the right pane header has a small `hex:` box — type an offset and press Enter to jump to it.

### 1.4 What the colors mean

Every row gets a 2px accent bar plus a background tint:

| Class | Color | Meaning |
|---|---|---|
| **Added** | green | Line exists only in the right (bottom) file |
| **Removed** | red | Line exists only in the left (top) file |
| **Modified** | amber | Both sides have a line here, content differs |
| **Gap** | cyan diagonal stripes | Filler row inserted to keep the two sides aligned (Show Gaps on) |
| Unchanged | plain | Identical on both sides |

In hex view with **Per-Byte** on, coloring drops to byte granularity: each of the 16 bytes per row is shaded red/green/amber individually, unchanged bytes on a mixed row are dimmed, and the ASCII pane tints along. The status bar then reports `Δ N line(s) differ (hex)`; if byte alignment had to fall back to positional matching in some regions it adds `⚠ k region(s) shown positionally`.

### 1.5 Status bar

Three segments:

- **Left:** `◈ name  N lines  size  modified-time` (or `◈ (no left file)`)
- **Right:** same for the right file
- **Info:** diff result + context, e.g.
  - `✓ Files are identical`
  - `Δ 12 changes | Layout: Side-by-Side | Scroll: Synced | [Ctrl+L] Left [Ctrl+Shift+R] Right [Ctrl+R] Refresh [Ctrl+S] Swap [F11] Layout`
  - `⧖ Computing hex diff... (a vs b bytes)` while a large hex diff runs, with a progress bar overlay after ~0.5 s and a **✕ Cancel** button after 1 s (hex computation only)

### 1.6 Scrolling, gaps, and anchor markers

- With **Sync Scroll** on, scrolling one pane scrolls the other to the same *proportion* of its content, so it holds even when the files are different sizes. Horizontal scroll is linked the same way.
- **Show Gaps ON:** alignment filler rows are drawn as cyan diagonal-stripe bands; both sides stay line-for-line aligned at all times.
- **Show Gaps OFF (compact):** gap rows are removed, so a change block in one pane has no physical counterpart rows in the other. Instead:
  - Each Added/Removed block carries an **≈N chip** at the right edge of its first row — N is the line number on the *other* side where this change corresponds ("the partner boundary is around line N").
  - **Double-clicking** a change block draws a thin full-width "slot" line in the partner pane at the corresponding boundary — *"a gap goes here."*

### 1.7 Sync points (manual alignment anchors)

When the diff engine misaligns two files (common with repetitive code), you can pin your own anchors:

1. Right-click a text line → **🔗 Set Sync Point** (in hex: **🔗 Set Sync Point @ 0x…**, at byte granularity).
2. The status bar asks you to click the corresponding line in the *other* file; that row shows a dashed `◀ PENDING` marker.
3. Click the matching line there to confirm.

Rules: in text mode both lines must have **identical content** (otherwise a warning dialog shows both lines); in hex mode the two offsets just need to preserve order on both sides (a crossing pair is rejected with a warning). Confirmed points show a `◈ SYNC` gutter marker and a dashed line, and steer the diff engine's alignment. Manage them from the toolbar (`# N`, **Clear All**), remove one via right-click → **✕ Remove Sync Point Here**, or press `Escape` to cancel a pending confirmation. Loading new files clears all sync points; **Swap** flips them side-for-side.

### 1.8 Structural analysis (text only)

With `▤ Structure` checked, rows whose *structure* changed get a small triangle in the gutter left of the line number:

- **Red** — brace imbalance / invalid document
- **Green** — added function / control block / type / JSON key / XML element
- **Rose** — removed ones
- **Amber** — changed signatures, lambdas, loops, other structural edits

Hover a triangle for the specific note (e.g. "signature changed"). This is advisory — it never changes the diff itself.

### 1.9 The diff map overview bar

A 4-pixel-wide vertical strip just left of the scrollbar shows colored markers for every change block, positioned proportionally to line number, with a small ring/dot indicating your current scroll position. It's a visual overview only — scroll by dragging the scrollbar thumb.

### 1.10 Editing the right pane

- **Text view:** tick *edit* (right pane header) to turn the right pane into an editable text box; `Ctrl+S` then saves it, and the `! Unsaved Changes ▾` toolbar menu offers **Save Right / Save As... / Discard Changes**.
- **Hex view:** edit mode enables direct byte editing — click/drag to select bytes (or whole rows), type to overwrite, `Delete` removes selected bytes. The **Hex Inspector** panel below the panes interprets the current selection as a typed value (int8…double, char; little-endian; multi-byte selections render as arrays) and, with edit mode on, lets you type new values or **⊕ insert** them inline.
  - **Custom struct patterns:** the **⚙ structs…** button in the inspector opens a manager where you define named structs built from the inspector's types (each field a type repeated N times, with little- or big-endian byte order). Pick one from the Type dropdown and the selection is read as an array of that struct — one row per field; with edit mode on each field is individually editable. Patterns are saved for reuse across sessions.
- Right-click a **Modified/Added/Gap** line in the right text pane for an **Apply** menu: *Replace with left version*, *Remove this line*, *Insert missing line from left* — or, when clicking inside a whole change block, *Revert this change (N lines)* / *Remove this change (N lines)*, plus **Apply all changes**.
- Right-click any hex row to compute the **CRC-32** of the selected byte(s) in a small dialog.

### 1.11 Diff engine choice

Right-click any text line → at the bottom of the menu: **⚙ Myers diff (faster)** / **⚙ Patience diff (default)**. The check mark shows the active engine; the choice applies to the next computation.

---

## 2. 3-Way Merge Window

Title bar: `AwfulDiff // 3-Way Merge`. Resolves conflicts between a base file and two changed copies — this is the window VCS tools (Perforce, git, SourceTree) launch on your behalf (see [Section 5](#5-command-line--vcs-integration)).

### 2.1 Starting a merge

- **From the main window** — `[⋔] 3-Merge...` or `Ctrl+M` opens the **Merge Setup** dialog: three rows (**Base**, **File A**, **File B**), each with a path box and drop target (you can drag files onto a row). File A and File B are pre-filled with the files currently open in the main window; **OK** stays disabled until all three paths are set. The dialog remembers its size between uses.
- **From the command line** — `AwfulDiff.exe base fileA fileB [-o output]` opens the merge window directly, skipping the setup dialog.

### 2.2 Layout

Three panes side by side — **File A | base | File B** — separated by draggable dividers. Each pane header shows the file name and a meta line (line count / size). The middle *base* pane has an `edit` checkbox: ticking it replaces the viewer with a free-text editor for the merged output, so you can hand-edit regions the engine couldn't classify. Your edits survive re-resolving conflicts around them.

The legend at the right of the toolbar explains the four tints used in the panes:

| Swatch | Meaning |
|---|---|
| **File A** | Region changed only on side A — taken from A automatically |
| **File B** | Region changed only on side B — taken from B automatically |
| **Conflict** | Both sides changed the same region differently — needs a resolution |
| **Resolved** | A conflict you have already resolved (✓ glyph + green bar + diff-map marker) |

Right-clicking in the base pane offers **⇄ Base pane: original base / merged output** to flip what the middle pane displays.

### 2.3 Navigating

| Keys | Action |
|---|---|
| `F3` / toolbar `Next conflict ▶` | Jump to the next unresolved conflict (all three panes scroll together) |
| `Ctrl+F3` / toolbar `◀ Prev conflict` | Previous unresolved conflict |
| `Shift+F3` / toolbar `Resolved ▶` | Next *resolved* conflict |
| `Ctrl+Shift+F3` / toolbar `◀ Resolved` | Previous resolved conflict |
| `Ctrl+Down` / `Ctrl+Up` | Walk the non-conflict change units (single-side changes) |

The toolbar shows a live `{n} unresolved` counter.

### 2.4 Resolving conflicts

Right-click a line inside a **conflict** in pane A or pane B:

| Menu item | Effect |
|---|---|
| `⚠ Take A's change (n)` | Resolve the slot to side A's lines |
| `⚠ Take B's change (n)` | Resolve the slot to side B's lines |
| `⊕ Keep merged base (n)` | Keep the original base content for this slot (nothing, if both sides only added lines) |
| `Take A before B (n)` / `Take B before A (n)` | Keep *both* sides' lines, in the chosen order — applies to your selection when there is one, otherwise to the whole conflict block |
| `↺ Reset selected to unresolved` / `↺ Reset to unresolved` | Undo a resolution and mark the slot as a conflict again |

The `(n)` count appears when lines are selected. Single-side (non-conflict) changes are applied automatically; right-click one of them to **✓ Accept this change** or **✗ Reject this change** (reject reverts that hunk to the base content), or use `✓/✗ … selected (n)` with a selection.

Bulk actions on the toolbar: **Accept All**, **Reject All**, and snapshot-based **↺ Undo** (`Ctrl+Z`) / **↻ Redo** (`Ctrl+Y` or `Ctrl+Shift+Z`) — undo covers resolutions, accept/reject sets, and base-pane edits (a run of typing counts as one step).

### 2.5 Saving and closing

- **Save base (`Ctrl+S`)** writes the merged content (UTF-8, no BOM) to the output file when one was given on the command line, otherwise over the base file. The button is disabled while any conflict is unresolved.
- Closing with **unresolved conflicts** prompts: *Yes — keep this window open and continue resolving / No — exit now and discard all merge changes* (the base or output file is left untouched).
- Closing with **unsaved changes but no conflicts** prompts **Save / Don't Save / Cancel**.
- When launched from the command line with an explicit output path (`-o` or a 4th positional), a *clean* merge (everything resolved, no edits pending) is written automatically on close even if you never pressed Save; discarding or leaving conflicts unresolved exits with code **1** so the VCS tool falls back to its own merge.

---

## 3. Directory Diff Window

Title bar: `AwfulDiff // Directory Diff`. Recursively compares two folders and lists every difference.

### 3.1 Starting a comparison

- **From the main window** — `[▣] Directories...` opens this window pre-loaded with your most recent folder pair. **Tip:** hold **Ctrl** while clicking it to compare the *parent folders* of the two files currently open in the main window instead.
- **Drag and drop** — drop a folder onto the main window, or directly onto either path box here (the left zone fills the left path, the right zone the right one).

### 3.2 Toolbar

`◄ [left path] Browse... vs [right path] Browse... ►`, then on the right:

| Control | What it does |
|---|---|
| `Show unchanged files` | Include byte-identical files in the tree (off by default) |
| `Show unchanged folders` | Include folders with no differences (off by default) |
| `[⇄] Swap` | Exchange left/right folders and re-compare |
| `[▣] Compare` | Run the comparison for the current paths |

Both toggles re-filter the already-scanned tree instantly (no re-scan) and are remembered between sessions. The path boxes are editable — type a path and hit **Compare**.

### 3.3 The results tree

Each row shows: status glyph, name (hover for the full relative path), size on each side, and modification time on each side. Folders expand/collapse like a normal tree (expanded by default). Row colors:

| Color | Meaning |
|---|---|
| dimmed | identical file / folder with no differences |
| amber | file differs between the two sides |
| red | only in the left folder |
| green | only in the right folder |
| accent (cyan) | folder that contains differences |

If nothing is visible after a compare, the tree shows *No differences found — the directories are identical*.

### 3.4 Working with results

- **Double-click** a file row to open it in the main window (both sides when present on both) — if the main window was closed, it is reopened for you.
- **Right-click** a row: **Show in Explorer** (opens Explorer with the file selected), **Expand all / Collapse all** under the cursor.
- The status bar reports the scan summary (file counts by category and elapsed time); a `⚠ N warning(s)` counter appears when anything was unreadable — hover it for the details.

The window remembers its size/position and your recent folder pairs (up to 8), which is what `[▣] Directories...` re-opens.

---

## 4. Settings

Open with `⚙ Settings` in the main window toolbar. The dialog has **Apply** (persist) and **Cancel** (discard); changes preview live while you adjust them. Everything is stored per-machine in `%APPDATA%\AwfulDiff\settings.json`.

### 4.1 Theme

| Option | Appearance |
|---|---|
| **Awful — Sci-fi holographic (default)** | Dark neon theme with the custom borderless window chrome and themed scrollbars |
| **System Light** | Standard Windows light appearance, standard window chrome |
| **System Dark** | Standard Windows dark appearance, standard window chrome |

### 4.2 Font scale

A slider from **50% to 200%** (10% steps) with a live percentage readout and quick-set buttons for 75 / 100 / 125 / 150 / 200%. Scales the UI font size across all windows.

### 4.3 Diff engine

- **Myers** — O(N+D²), faster on large files
- **Patience** — anchors on lines unique to both files (default); usually produces tighter, more intuitive change blocks

The same choice is available per-computation from the right-click menu in the main window; this setting changes the default.

### 4.4 Syntax coloring

*Use Visual Studio-style syntax colours (desaturated)* toggles keyword/string/comment/number/function-call/type coloring in text view (muted palette that won't fight the diff colors). Function calls are detected as an identifier followed by `(`, and types as capitalized (PascalCase) identifiers — heuristics, like VS's own display. Hex view is never colored.

### 4.5 Custom colours

Per-theme overrides for the six diff roles — *unticked = theme default*:

| Row | Role |
|---|---|
| Adds | Added lines |
| Removes | Removed lines |
| Modifications | Modified lines |
| Merge candidates (A & B) | Single-side changes in the merge window |
| Merge conflicts | Unresolved conflict regions |
| Standard text | Plain unchanged text |

Tick a row's checkbox and click its swatch to open the **colour picker**: R/G/B sliders plus a hex field (strict `#RRGGBB` or `#AARRGGBB`) with a live preview. Overrides are stored separately for each theme, so your Awful-theme colors don't leak into System Dark.

---

## 5. Command Line & VCS Integration

AwfulDiff takes positional file paths and reports success through its process exit code, which is what makes it usable as an external diff/merge tool.

### 5.1 Usage

```
AwfulDiff.exe [left] [right]
    Opens a 2-way diff window (0 to 2 files).

AwfulDiff.exe base fileA fileB [-o output]
    Opens a 3-way merge window directly. Argument order is base-first
    (matches Perforce p4merge and Meld). The merged result is saved to
    the base file, or to 'output' when given via -o or as a 4th path.

Options:
  -h, --help             Show this help text.
  -o, --out, --output    Merge output file (instead of the base file).

Exit codes:
  0   diff mode, or merge mode where the merged result was written to disk
  1   merge mode where the result was discarded / not written, or a load failure
```

How arguments map to windows:

| Arguments | Result |
|---|---|
| *(none)* | Empty main window — open files from the toolbar |
| `left` or `left right` | 2-way diff window (1–2 files) |
| `base fileA fileB` | 3-way merge window, **base first** |
| `base fileA fileB -o out` *(or a 4th bare path)* | Merge window; result written to `out` instead of the base file |

Behavioral notes:

- **Unknown `-flags` are silently ignored** — VCS tools that pass extra flags won't break the launch.
- File paths must not start with `-` (such tokens are treated as flags).
- In merge mode, closing with everything resolved and no edits auto-writes the result *only when an output path was given* (`-o` or 4th positional); with no output path the base-file write happens through the normal save prompt. Discarding, leaving conflicts unresolved, or a load failure yields exit code **1** so the VCS tool can fall back to its own merge.

### 5.2 Perforce

Set `P4DIFF` and `P4MERGE` in your client environment (e.g. via `p4 set` or your shell profile). Merge arguments are base-first: `%P1`=base, `%P2`=your changes, `%P3`=their changes, `%P4`=output.

```bat
set P4DIFF="C:\Tools\AwfulDiff\AwfulDiff.exe %P1 %P2"
set P4MERGE="C:\Tools\AwfulDiff\AwfulDiff.exe %P1 %P2 %P3 %P4"
```

Perforce treats a non-zero exit from the merge tool as "merge failed — keep conflict state," which is exactly what AwfulDiff reports when you discard or leave conflicts unresolved.

### 5.3 git

Add to your `.gitconfig`:

```ini
[difftool "awfuldiff"]
    cmd = AwfulDiff "$LOCAL" "$REMOTE"
[mergetool "awfuldiff"]
    cmd = AwfulDiff "$BASE" "$LOCAL" "$REMOTE" --out "$LOCAL"
    trustExitCode = true
```

Then run:

```bash
git difftool -t awfuldiff        # compare local vs remote (or a commit)
git mergetool -t awfuldiff       # resolve the current merge with AwfulDiff
```

`trustExitCode = true` is what lets git honor exit code 1 (discarded/unresolved merge) and re-prompt instead of accepting a failed merge.

### 5.4 SourceTree

In **Tools → Options → Git**, set the external diff tool and external merge tool to the full path of `AwfulDiff.exe`. SourceTree passes arguments in the same base-first order (base, your changes, their changes, output), so no extra configuration is needed — the exit-code contract above applies.

### 5.5 Examples

```powershell
# Compare two files directly
AwfulDiff.exe config.json config.new.json

# Merge: result written back over the base file
AwfulDiff.exe base.c mine.c theirs.c

# Merge: write to a separate output file (base stays untouched)
AwfulDiff.exe base.c mine.c theirs.c -o resolved.c
```
