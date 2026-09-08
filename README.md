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
  page — "in accordance with 8.3.1", "see 6.2.5", "Annex L", "Figure 8.5", "Table 4.3" —
  get a faint underline and become clickable, jumping to the referenced clause or to the
  figure / table's page (matched by its caption).
- **Extract table**: arm the toolbar toggle, drag a rectangle over a table, and DocDr
  reconstructs its rows and columns — into a review grid you can **Copy (TSV)** into a
  spreadsheet or **Save as CSV**. Rows and columns come from the table's ruled lines or, when
  it has none, from the whitespace channels between cells. Draw the box tight around the table
  body; nested multi-row headers may need a little tidying, but the data rows come out clean.
- **Cite this**: right-click a clause in the index for **Copy citation**
  (`{file}, cl. 6.5 (Concrete cover), p. 88`); or select text on a page and click **Cite**
  in the popup to copy the quoted passage with its source, nearest clause, and page.

### Stage 6 — OCR (done)

- **OCR…** (toolbar): for a scanned PDF with no text layer, DocDr renders each text-less page
  at 300 DPI, recognises it with Tesseract 5 (bundled English model), and bakes the words back
  in as an **invisible text layer** — so search, text selection, clause detection, citation and
  chunk export all start working on the scan. Pages that already have text are left alone.
- A progress dialog shows the page count and can be cancelled; cancelling keeps the pages done
  so far (re-run to finish the rest). Like watermark removal this edits page content and is not
  undoable — the original file is safe until you save. ~2 s per page.

### RAG chunk export (Stage 5 — partial)

- **Export chunks…** (toolbar) splits the document's text into overlapping, retrieval-sized
  passages and writes them as **JSONL** (one JSON object per line) for building a RAG index.
- Sections come from the detected clause tree, or the bookmark outline, or the whole document;
  each chunk carries a stable `id`, `text`, `source_path`, `doc_title`, `page_start`,
  `page_end`, `section_title`, `chunk_index` and a `token_estimate`. Default ~600 tokens per
  chunk (chars ÷ 4) with ~15% overlap. The `text` is prefixed with the document title and
  section heading so the embedding carries where the passage sits.
- Running headers / footers (lines that repeat across most pages), bare page numbers and
  contents-page leaders are stripped; wrapped lines are joined and words de-hyphenated across
  line breaks. Pages with no text layer are reported and skipped — run **OCR…** first.

### Merge & bookmarks

- **Merge…** (home screen / toolbar): pick several PDFs, put them in order and give each a
  bookmark title in the dialog, and DocDr combines them into one new document (opens in a new
  tab — Save As to keep it). "Add a bookmark for each file" also pulls in numbered sub-headings
  (`5.1 Introduction`, …) under each file.
- **Generate from headings** (Bookmarks panel, when a document has none): builds an outline from
  detected clause/section headings.
- Generated bookmarks are written into the PDF on save, so other viewers see them too. Clause
  and figure cross-references are re-detected every time a document is opened — nothing to save.

### Page handling

- **Printed page labels** — a standard with roman front matter or prefixed appendix numbering
  ("vii", "A-3") shows that label in the page box, the navigation panels and citations.
- **Trim margins** (toolbar): crops every page to its visible content plus a small margin —
  handy for scans and merged documents. One undo step; the crop is saved into the file.
- **Bare URLs are clickable** — an `http`/`https`/`mailto` address printed as plain text opens
  in your browser, even without a real link in the PDF.
- Document Properties shows whether a PDF is tagged (accessible), encrypted, or re-labelled.

### Collage authoring

- **New document…** (home screen / toolbar): a blank page at A4 / A3 / A2 / A1 or a custom mm
  size, portrait or landscape. Opens untitled in a new tab — Save As to keep it.
- **Paste** (`Ctrl+V`): a screenshot or copied image lands as a movable, resizable picture in the
  middle of the visible area; copied text lands as a text box. Paste analysis plots and results
  tables side by side and annotate them.
- **Shapes**: rectangle, ellipse, line and arrow tools alongside the existing callout / cloud /
  text-box tools — drag to place, then move / resize / recolour. Everything is saved into the PDF
  so other viewers see the finished collage.
- **Format toolbar**: select any shape, line, text box, cloud, highlight or note and a small
  floating bar appears with the controls that kind supports — outline colour, line thickness,
  dashed on/off, fill colour, and for text boxes the text colour (black by default or matching the
  outline), a border toggle and a font-size stepper. Lines and arrows drag by their end points.
- **Custom colour**: a sixth "Custom…" swatch on the colour rail (and in the format bar) opens a
  picker; the exact colour is remembered and saved into the PDF.

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

Not yet (later stages): folder browser, SQLite catalog + duplicate finder.

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
