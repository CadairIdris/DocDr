# DocDr

A Windows desktop PDF viewer, editor, and cataloguing tool. Built in stages
(see [`_specs/001-pdf-desktop-app-spec.md`](_specs/001-pdf-desktop-app-spec.md)).

## Status — Stage 1 (Core Viewer + PDFium plumbing)

Done:

- Load a PDF via PDFium (`PDFiumCore`, Apache-2.0) and render pages to WPF.
- Tabbed shell — several documents open at once, the same file openable in more than one tab.
- Each tab has **two independent panes** on one shared document: independent page position,
  zoom, view mode, and text search. Searching or navigating in one pane never touches the other.
- Single-page and continuous (virtualised) view modes.
- Per-pane full-text search: all matches, highlight overlays, match counter, next/previous.
- Zoom (25–800%, Fit Width, Fit Page, 100%) and page navigation.

Not yet (later stages): page editing, annotations, folder browser, metadata editing,
SQLite catalog + duplicate finder, OCR.

## Layout

| Project | Purpose |
|---|---|
| `src/DocDr.Pdf` | PDFium wrapper: `PdfiumLibrary`, `PdfDocument`, `PageRenderer` (+ LRU cache), `PdfSearch`, `PdfTextExtractor`, `PdfMetadata`. All PDFium calls are serialised process-wide. |
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
