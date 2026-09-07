# DocDr

A Windows desktop PDF viewer, editor, and cataloguing tool. Built in stages
(see [`_specs/001-pdf-desktop-app-spec.md`](_specs/001-pdf-desktop-app-spec.md)).

## Status

### Stage 8 — Design-code navigation (in progress)

- **Clause index**: the Navigator panel has a new tab (tree icon) that lists the numbered
  clauses of a standard — detected from the page text, so it works even when the PDF has no
  bookmarks. Tuned for dotted-decimal numbering (Eurocode, BS EN, ISO); annexes are picked up
  too. The scan runs in the background on open. Clicking a clause jumps the left pane to its
  page. Codes with letter-section numbering (e.g. AISC `D1.2a`) are not covered yet.
- **Clickable cross-references**: once the clause index is built, textual references in the
  page — "in accordance with 8.3.1", "see 6.2.5", "Annex L" — get a faint underline and
  become clickable, jumping to the referenced clause. Figure / table references are not
  linked (their numbers aren't clause numbers).
- **Cite this**: right-click a clause in the index for **Copy citation**
  (`{file}, cl. 6.5 (Concrete cover), p. 88`); or select text on a page and click **Cite**
  in the popup to copy the quoted passage with its source, nearest clause, and page.

### Dark mode

- **System / Light / Dark** picker in the toolbar, persisted to `%APPDATA%\DocDr\settings.json`.
  System follows the OS setting live.
- Built on .NET 9's `Application.ThemeMode` (Fluent light/dark for the standard controls) plus
  a small `Palette.Light` / `Palette.Dark` resource dictionary for DocDr's own surfaces.
- PDF pages stay white; `MessageBox` dialogs stay OS-styled.

### Document properties (Stage 4 — partial)

- **Properties…** dialog: view and edit the Info-dictionary fields (Title, Author, Subject,
  Keywords, Creator), with Producer / dates / page count / file size shown read-only.
- PDFium has no metadata *setter*, so on save DocDr appends an incremental-update Info object
  to the bytes PDFium produced. This also re-attaches metadata that a structural edit
  (rebuild) would otherwise drop, and stamps `ModDate`.

### Stage 3 — Annotations (done)

- **Highlight text**: drag to select text on a page, then pick one of five colours from
  the popup. **Note**: "Note…" in that popup attaches a note to the highlight, or the
  **💬 Note** toolbar toggle drops a standalone note pin where you click — drag the pin to
  move it, double-click to edit.
- **Freehand highlighter** (**✏️ Draw**): drag anywhere on the page to draw a marker-pen
  stroke in the chosen colour; written out as a standard PDF `Ink` annotation.
- Click any annotation to select it (accent outline / glow + trash button; `Delete` also
  works). The nav panel's **Annotations** tab lists every one — labelled by kind — and
  clicking a row jumps to it and highlights it on the page.
- **Reply threads**: a note (or noted highlight) can carry a conversation — the editor
  shows the replies, each with its author and time, and a box to add another. The whole
  thread is stored on the annotation and round-trips through save/reload; other PDF readers
  still show the opening note. The Annotations tab flags rows that have replies.
- **Text box / callout / cloud** (toolbar toggles): a **text box** (drag a rectangle) is a
  bordered box of text in a chosen colour and size; a **callout** (drag from what you're
  pointing at to where the note goes) adds a leader line with an arrowhead; a **revision
  cloud** (drag a rectangle) outlines a changed area with a scalloped border. Text boxes and
  callouts shrink to fit their text until you resize them. Select a box to move it or drag a
  corner handle to resize it; a selected callout also has a round handle on its arrow tip — drag
  it to re-point the arrow. Double-click a box to edit; `Delete` to remove. All three are saved as
  PDF stamps with a real appearance built from paths and text, so they print and show
  correctly in any reader.
- **👁 Markup** toolbar button hides / shows the whole overlay (highlights, notes, ink) —
  a view toggle; the annotations are untouched and still saved.
- Each annotation records its author and creation / modification dates in standard PDF
  fields, and DocDr reads that data back from files annotated in other apps.
- Annotations are DocDr's own model during a session (so they survive rotate / delete /
  insert / undo and stay on the right page); on save they are written into the PDF as
  standard `Highlight` / `Text` annotations, visible in any other reader.

### Remove watermark (Stage 4 — partial)

- **Watermark…** scans for content repeated across most pages — text stamps and
  near-full-page overlay images — and lists each with a before/after page preview.
  Tick what to strip; removal deletes just those operators from the page content
  streams (so kerned tables and the rest of the page are left byte-for-byte intact)
  and reloads the document. Not undoable — it clears the undo history, so save to a
  copy to keep the original.

### Stage 2 — Page editing (done)

- **Rotate / delete / insert pages.** Targets the thumbnail-strip selection when the Pages
  panel is showing it, otherwise the active pane's current page. Delete confirms first;
  Insert brings in every page of another PDF, or a **blank page** sized to match the page
  it follows (toolbar, or right-click a thumbnail).
- **Undo / redo** (`Ctrl+Z` / `Ctrl+Y`, ~30 deep). The document is modelled as an ordered
  list of page references into one or more source PDFs; structural edits rebuild the live
  PDFium handle, undo restores an earlier version of the list.
- **Save / Save As** (`Ctrl+S` / `Ctrl+Shift+S`). Files are loaded fully into memory so Save
  overwrites the original in place. Dirty tabs show `•` and prompt on close.
- **Print…** (`Ctrl+P`): a standard print dialog (printer, page range, copies); pages are
  rasterised through PDFium at print resolution and fitted to the sheet, from the current
  in-memory state — so page edits and the annotation overlay are included. Long ranges warn
  first (the window is unresponsive while the job spools).

### Stage 1 — Core viewer + PDFium plumbing (done)

- Load a PDF via PDFium (`PDFiumCore`, Apache-2.0) and render pages to WPF.
- Tabbed shell — several documents open at once, the same file openable in more than one tab.
- Home screen with a **recent documents** list (persisted between sessions).
- Optional **split view** (off by default): a second, fully independent pane on the same
  document — independent page position, zoom, view mode, and text search.
- Three view modes per pane: single-page, continuous (virtualised), and grid (rows of pages,
  column count auto-fits the width at the current zoom).
- **Read mode** (`⛶ Read` / `F11`): full-screen, chrome-free, one two-page spread at a time
  sized to fill the screen. Arrow keys / space / scroll turn the spread; `Esc` exits and
  restores the previous view.
- Collapsible, resizable **navigation panel** per tab (the **Navigator** toolbar toggle, off
  by default): an icon tab strip switches its section — page thumbnails, the bookmark outline,
  or the markup list (comments, highlights, ink) — and clicking a row jumps to it.
- **Clickable in-document links**: a table-of-contents entry, cross-reference, or URL in the
  page (a PDF `/Link` annotation) is followed on click — internal links jump to the page,
  `http`/`https`/`mailto` links open in the browser.
- Per-pane full-text search: all matches, highlight overlays, match counter, next/previous.
- Zoom (25–800%, Fit Width, Fit Page, 100%, **Ctrl+wheel / trackpad pinch**, anchored on the
  cursor) and page navigation.

Not yet (later stages): folder browser, SQLite catalog + duplicate finder, OCR.

## Layout

| Project | Purpose |
|---|---|
| `src/DocDr.Pdf` | PDFium wrapper: `PdfiumLibrary`, `PdfDocument` (in-memory load, page edit + undo/redo + save + metadata + annotations), `PageRenderer` (+ LRU cache), `PdfSearch`, `PdfTextExtractor`, `PdfMetadata` (+ `PdfMetadataWriter`, `PdfDate`), `PdfBookmarks`, `PdfLinks`, `PdfAnnotations` (+ `PdfAnnotationWriter`), `PdfWatermarks` (+ `PdfWatermarkStripper`). All PDFium calls are serialised process-wide. |
| `src/DocDr.App` | WPF app (MVVM via CommunityToolkit.Mvvm): tab shell, panes, background render queue. |
| `tests/DocDr.Pdf.Tests` | xUnit tests over `DocDr.Pdf`, including concurrency stress tests. Fixtures are generated at test time (`TestPdfBuilder`). |

## Build & run

```
dotnet build
dotnet test
dotnet run --project src/DocDr.App              # empty shell
dotnet run --project src/DocDr.App -- a.pdf b.pdf   # opens files as tabs
```

Requires the .NET 9 SDK and Windows (x64). PDFium native binaries come with the `PDFiumCore`
NuGet package.

## Publish

`publish.ps1` produces a `win-x64` build under `publish/`:

```
pwsh publish.ps1                                       # self-contained folder (~140 MB)
pwsh publish.ps1 -SingleFile                           # self-contained, one .exe (~150 MB)
pwsh publish.ps1 -Mode framework-dependent             # folder, needs the .NET 9 Desktop Runtime (~6 MB)
pwsh publish.ps1 -Mode framework-dependent -SingleFile # one .exe, needs the runtime (~5 MB)
```

Self-contained runs on a machine with no .NET installed; framework-dependent is far
smaller but needs the .NET 9 Desktop Runtime. `-SingleFile` works with either mode and
packs everything (pdfium.dll included) into one `DocDr.App.exe` that self-extracts on
first run. The first three are also in `.vscode/tasks.json`, and `SelfContained.pubxml` /
`FrameworkDependent.pubxml` under `src/DocDr.App/Properties/PublishProfiles` cover the VS /
Rider publish UI. `ReadyToRun` is on only for self-contained (on a framework-dependent
target the runtime patch can differ, and R2R + single file fail-fasts instead of falling
back to JIT); WPF can't be trimmed so trimming stays off.

## Threading note

PDFium (the `pdfium-binaries` build) is not safe for concurrent use, even across different
documents. Every call goes through one process-wide lock in `PdfiumLibrary.SyncRoot`;
rendering happens on a single background worker behind that lock. Do not add lock-free or
per-document concurrent rendering.

## Licence

DocDr is [MIT](LICENSE). It bundles third-party components under their own permissive
licences — PDFiumCore (Apache-2.0), PDFium (BSD-3-Clause), CommunityToolkit.Mvvm (MIT);
see [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), which `publish.ps1` copies into
every build.
