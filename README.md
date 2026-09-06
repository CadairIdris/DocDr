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

### Stage 3 — Annotations (done)

- **Highlight text**: drag to select text on a page, then pick one of five colours from
  the popup. **Comment**: "Comment…" in that popup attaches a note to the highlight, or the
  **💬 Note** toolbar toggle drops a standalone sticky note where you click.
- Click a note marker to edit or delete; the nav panel's **Annotations** tab lists every
  annotation (with its author and date) and jumps to it.
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
  Insert brings in every page of another PDF.
- **Undo / redo** (`Ctrl+Z` / `Ctrl+Y`, ~30 deep). The document is modelled as an ordered
  list of page references into one or more source PDFs; structural edits rebuild the live
  PDFium handle, undo restores an earlier version of the list.
- **Save / Save As** (`Ctrl+S` / `Ctrl+Shift+S`). Files are loaded fully into memory so Save
  overwrites the original in place. Dirty tabs show `•` and prompt on close.

### Stage 1 — Core viewer + PDFium plumbing (done)

- Load a PDF via PDFium (`PDFiumCore`, Apache-2.0) and render pages to WPF.
- Tabbed shell — several documents open at once, the same file openable in more than one tab.
- Home screen with a **recent documents** list (persisted between sessions).
- Optional **split view** (off by default): a second, fully independent pane on the same
  document — independent page position, zoom, view mode, and text search.
- Three view modes per pane: single-page, continuous (virtualised), and grid (rows of pages,
  column count auto-fits the width at the current zoom).
- Collapsible, resizable **navigation panel** per tab (off by default): an icon tab strip
  for page thumbnails / the bookmark outline (and annotations, from Stage 3); click either
  to jump to that page.
- Per-pane full-text search: all matches, highlight overlays, match counter, next/previous.
- Zoom (25–800%, Fit Width, Fit Page, 100%, **Ctrl+wheel / trackpad pinch**, anchored on the
  cursor) and page navigation.

Not yet (later stages): folder browser, SQLite catalog + duplicate finder, OCR.

## Layout

| Project | Purpose |
|---|---|
| `src/DocDr.Pdf` | PDFium wrapper: `PdfiumLibrary`, `PdfDocument` (in-memory load, page edit + undo/redo + save + metadata + annotations), `PageRenderer` (+ LRU cache), `PdfSearch`, `PdfTextExtractor`, `PdfMetadata` (+ `PdfMetadataWriter`, `PdfDate`), `PdfBookmarks`, `PdfAnnotations` (+ `PdfAnnotationWriter`). All PDFium calls are serialised process-wide. |
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
