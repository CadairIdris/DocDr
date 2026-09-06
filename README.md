# DocDr

A Windows desktop PDF viewer, editor, and cataloguing tool. Built in stages
(see [`_specs/001-pdf-desktop-app-spec.md`](_specs/001-pdf-desktop-app-spec.md)).

## Status

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

### Stage 2 — Page editing (done)

- **Rotate / delete / insert pages.** Targets the thumbnail-strip selection when the Pages
  panel is showing it, otherwise the active pane's current page. Delete confirms first;
  Insert brings in every page of another PDF.
- **Undo / redo** (`Ctrl+Z` / `Ctrl+Y`, ~30 deep). The document is modelled as an ordered
  list of page references into one or more source PDFs; structural edits rebuild the live
  PDFium handle, undo restores an earlier version of the list.
- **Save / Save As** (`Ctrl+S` / `Ctrl+Shift+S`). Files are loaded fully into memory so Save
  overwrites the original in place. Dirty tabs show `•` and prompt on close.

### Stage 1 — Core viewer + PDFium plumbing (done)

- Load a PDF via PDFium (`PDFiumCore`, Apache-2.0) and render pages to WPF.
- Tabbed shell — several documents open at once, the same file openable in more than one tab.
- Optional **split view** (off by default): a second, fully independent pane on the same
  document — independent page position, zoom, view mode, and text search.
- Three view modes per pane: single-page, continuous (virtualised), and grid (rows of pages,
  column count auto-fits the width at the current zoom).
- Collapsible **navigation panel** per tab (off by default): page thumbnails or the
  document's bookmark outline; click either to jump to that page.
- Per-pane full-text search: all matches, highlight overlays, match counter, next/previous.
- Zoom (25–800%, Fit Width, Fit Page, 100%, **Ctrl+wheel / trackpad pinch**, anchored on the
  cursor) and page navigation.

Not yet (later stages): annotations, folder browser, metadata editing,
SQLite catalog + duplicate finder, OCR.

## Layout

| Project | Purpose |
|---|---|
| `src/DocDr.Pdf` | PDFium wrapper: `PdfiumLibrary`, `PdfDocument` (in-memory load, page edit + undo/redo + save + metadata), `PageRenderer` (+ LRU cache), `PdfSearch`, `PdfTextExtractor`, `PdfMetadata` (+ `PdfMetadataWriter`, `PdfDate`), `PdfBookmarks`. All PDFium calls are serialised process-wide. |
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

## Threading note

PDFium (the `pdfium-binaries` build) is not safe for concurrent use, even across different
documents. Every call goes through one process-wide lock in `PdfiumLibrary.SyncRoot`;
rendering happens on a single background worker behind that lock. Do not add lock-free or
per-document concurrent rendering.
