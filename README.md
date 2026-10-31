# DocDr

A Windows desktop PDF viewer, editor and markup tool, built for working with large engineering
standards and design documents. It can also export them as clean text for RAG and for AI agents.

## Features

### Viewing

- **Tabs and a home screen** with recent documents; open the same file in several tabs, or drag a
  tab to the right to read **two documents side by side**, with optional **linked scrolling**.
- **View modes** per pane: continuous, single page, two-page spread and a page grid. **Zoom**
  25–800 %, fit width / page, Ctrl+wheel or trackpad pinch anchored on the cursor. Very high zoom
  re-renders the visible part of the page at full resolution, so it stays sharp.
- **Read mode** (`F11`): full screen, no chrome, one spread at a time.
- **Split view**: a second independent pane on the same document.
- A slim **status strip** at the bottom of each pane holds page navigation, zoom and view mode.
- **Navigation rail** with panels for page thumbnails, bookmarks, clauses, comments and
  annotations.
- **Printed page labels** ("vii", "A-3") are used in the page box, panels and citations.
- **Clickable links**: PDF links, bare URLs printed in the text, and clause / figure / table
  cross-references (once the clause index is built).
- **Dark mode**: System / Light / Dark (pages stay white).

### Search

- **Find bar** (`Ctrl+F`): matches stream in while the search runs, highlights are one padded box per
  line, and the current match is scrolled to the centre. Phrases split across a line break are found.
  Each page's text is extracted once and reused, so repeat searches are fast.
- **Find in files** (`Ctrl+Shift+F`): search every PDF in a folder (optionally subfolders) with
  snippets; clicking a result opens the document at that hit.

### Design-code navigation

- **Clause index**: detects the numbered clauses and annexes of a standard (Eurocode / BS EN / ISO
  dotted-decimal numbering) even without bookmarks; click to jump. The scan runs on demand and is
  cached in the file when you save.
- **Cross-references** like "see 6.2.5", "Annex L", "Figure 8.5" and "Table 4.3" become clickable.
- **Cite this**: copy a clause or a selected passage as a citation with file, clause and page.
- **Extract table**: drag a box over a table to rebuild its rows and columns; copy as TSV or save
  as CSV.

### Markup and comments

- **Highlights** in five colours or a custom colour, **notes**, **freehand ink**, **text boxes**,
  **callouts**, **revision clouds**, **rectangles / ellipses / lines / arrows**, and a **tick** tool.
  Select anything to move or resize it; a floating **format bar** sets colour, line width, dashes,
  fill, text colour, border and font size. Everything is undoable and saved as standard PDF
  annotations that other readers display.
- **Comments panel**: notes and commented highlights grouped by page, with **reply threads**,
  **review status** (Accepted / Rejected / Cancelled / Completed), a checkmark, and the author and
  date. Hover a note pin to read it without opening it.
- **Annotations panel** lists all other markup; the **Markup** toggle hides or shows the overlay.

### Measuring

- **Distance, perimeter and area** tools, click by click, with a live preview. **Snap** to the
  drawing's end points, intersections, midpoints and lines (Ctrl to suspend, Shift for 45°); vertices
  stay draggable and snap too.
- **Scale** per document or per page, set as a ratio (1:50) or **calibrated** from a known length.
  Each measurement keeps the scale it was taken at.
- **Export measurements** to CSV.

### Editing pages and documents

- **Rotate, delete, insert** (another PDF or a blank page), **trim margins**, with undo / redo.
- **Extract pages**: a selection, a range, or a whole chapter from a bookmark, as a new PDF with
  its annotations and bookmarks.
- **Merge** several PDFs into one, with a bookmark per file; **generate bookmarks** from detected
  headings; rename / delete bookmarks.
- **New document** (A4–A1 or custom size) and **paste** screenshots or text onto a page to build
  collages.
- **Remove watermark**: finds content repeated on most pages and strips only that, leaving the
  rest of the page byte-for-byte intact.
- **OCR** scanned pages (Tesseract, bundled English model) into an invisible text layer, so
  search, selection, clauses and exports work on scans.
- **Properties**: view and edit title, author, subject and keywords.
- **Save / Save As / Print**. Printing uses the current in-memory state, markup included.

### Export for agents and RAG

Both exports share the same text cleaning:
- running headers, footers and licence stamps are removed;
- contents pages are skipped;
- glyphs drawn twice and broken symbol-font characters are repaired where possible, and flagged
  where not;
- headings are tracked paragraph by paragraph.

- **Export chunks…**: overlapping, section-tagged **JSONL** chunks for a RAG index. Each chunk
  carries:
  - its id, text, source file, title, PDF pages and printed page labels;
  - its section and every section it spans;
  - the end of the previous chunk, as `context_before`;
  - every numbered **equation** in it, with a page bounding box;
  - a `quality` grade (`ok` / `check` / `suspect`) with plain-language `issues`: unmapped glyphs,
    scrambled equations, duplicated glyphs, flattened tables.
- **Export HTML…**: structured HTML with headings, paragraphs, notes and page anchors. Every
  equation, table and figure is an **image cropped from the page**, which is the authoritative
  version. The text layer sits alongside for searching, and tables also get a grid read from the
  text layer, marked unverified. Crops that may be cut off are flagged `crop_clipped`. A short
  "How to read this export" section explains the conventions to the agent.

## Layout

| Project | Purpose |
|---|---|
| `src/DocDr.Pdf` | PDFium wrapper and all document logic, WPF-free. It covers loading, rendering, editing with undo, saving, metadata, annotations, search, the text cache, clauses and cross-references, table extraction, watermark removal, measuring and snapping, extract / merge, and the chunk and HTML exports. All PDFium calls are serialised process-wide. |
| `src/DocDr.Ocr` | Tesseract OCR engine (kept separate so the native OCR dependency stays out of `DocDr.Pdf`). |
| `src/DocDr.App` | WPF app (MVVM via CommunityToolkit.Mvvm): tab shell, panes, panels, dialogs, background render queue. |
| `tests/DocDr.Pdf.Tests` | xUnit tests over `DocDr.Pdf`, including concurrency stress tests. |
| `tests/DocDr.App.Tests` | xUnit tests over the view models (Windows only). |
| `tests/DocDr.TestSupport` | `TestPdfBuilder`: test PDFs are generated at test time, so there are no binary fixtures in the repo. |

Developer notes (PDFium quirks, design decisions) live in [`CLAUDE.md`](CLAUDE.md); the original
staged plan is in [`_specs/001-pdf-desktop-app-spec.md`](_specs/001-pdf-desktop-app-spec.md).
Not built yet: folder browser, SQLite catalog and duplicate finder.

## Build & run

```
dotnet build
dotnet test
dotnet run --project src/DocDr.App              # empty shell
dotnet run --project src/DocDr.App -- a.pdf b.pdf   # opens files as tabs
```

Requires the .NET 9 SDK and Windows (x64). The PDFium bindings come from the `PDFiumCore` NuGet
package; the native binary is a current `pdfium-binaries` build (`bblanchon.PDFium.Win32`), not the
older one PDFiumCore bundles.

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
licences — PDFiumCore (Apache-2.0), PDFium (BSD-3-Clause), CommunityToolkit.Mvvm (MIT), and
Tesseract / Leptonica for OCR;
see [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), which `publish.ps1` copies into
every build.
