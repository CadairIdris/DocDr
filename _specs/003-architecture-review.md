# DocDr — Architecture Review

A stock-take of the design decisions made across the staged build, taken before continuing
feature work. The verdict: the foundation is sound and most of what looks like "fighting the
library" is a deliberate, well-tested trade-off. A few areas have accreted rather than been
designed, and those are where bugs keep coming from.

## Solid — leave alone

- **MVVM with `CommunityToolkit.Mvvm` source generators.** Modern, low-ceremony.
- **One process-wide lock around every PDFium call** (`PdfiumLibrary.SyncRoot` via
  `PdfDocument.Locked`). Not a choice — the `pdfium-binaries` build is not thread-safe even
  across different documents (shared font cache / allocator). One background render worker
  behind the lock. A thread-safe PDFium or per-document process isolation would be a huge
  effort for a desktop viewer that has 1–3 documents open.
- **Annotations as a WPF overlay, baked into the PDF only at save time**
  (`PdfStampAppearance`, `PdfShapeCodec`, `PdfReplyCodec`, `PdfAnnotationWriter`). Forced by
  PDFium having no setters for `/CL`, `/Vertices`, `/BE`, indirect-ref dict entries, etc. It is
  a large surface — every annotation feature is model + codec + appearance builder + overlay
  renderer + hit-test + persistence, kept in sync — but it is coherent and covered by tests.
  A different PDF library would be a worse renderer for a marginal annotation win.
- **Page-edit model: an ordered `List<PageRef>` rebuilt via `FPDF_CreateNewDocument` +
  `FPDF_ImportPages`.** Forced by `FPDF_ImportPages` failing when the destination is a
  `FPDF_LoadMemDocument` doc. Works and is well-tested. The cost: every new per-page attribute
  (rotation, and now `CropBox`) needs Rebuild-survival + undo plumbing — a discipline, not a
  redesign.
- **Staged spec (`_specs/001…`), SQLite catalog as the source of truth for user metadata
  (Stage 5).** Fine.

## Worth hardening — ranked

| # | Item | Status |
|---|---|---|
| 1 | **No automated coverage below `DocDr.Pdf`.** Every bug in the recent memory / rendering / toolbar work was in `DocDr.App` and caught only by hand — and synthetic input barely reaches WPF, so manual testing is weak. A `DocDr.App.Tests` over the *pure* view models (selection, format bar, render-request, coordinate maths — no WPF) closes it. | **done — `tests/DocDr.App.Tests` + `tests/DocDr.TestSupport`; `IRenderQueue` / `IAnnotationFormatHost` seams; `CachingPageRenderer` tests moved into `DocDr.Pdf.Tests`** |
| 2 | **Parallel scroll model.** `PdfPaneViewModel` kept its own `LayoutHeight`-sum offset maths (`GetPageAtOffset` / `GetRowAtOffset`) alongside WPF's virtualising-panel extent. They drift (float vs device-snapped; true sum vs averaged extrapolation) for a frame after a zoom/jump — the root of the blank-page-in-continuous and blank-thumbnail bugs, which were being patched piecemeal with "read the realised containers instead". | **done — one `VisiblePageRange()` reader for every mode; the offset methods deleted; `LayoutWidth/Height/RowHeight` kept as per-item template dimensions** |
| 3 | **`PdfPaneViewModel` is a ~3,100-line / ~130-member god object** — layout, zoom, render queue, search, text selection, every annotation kind, shapes, ink, format bar, links, cross-refs, paste, citations, table-select. Split into cohesive collaborators — candidates: `PageLayoutModel`, `SelectionModel`, `AnnotationInteractionController`, `RenderCoordinator` — each testable, each change with a smaller blast radius. | **incremental — split off one piece whenever that area is next touched; the `IRenderQueue` / `IAnnotationFormatHost` seams from #1 are the first cuts** |
| 4 | **Interaction flag-soup in `PdfPaneView.xaml.cs`** — ~11 mutually-aware `bool`s (`_selecting`, `_inking`, `_shaping`, `_movingShape`, `_resizing`, `_leaderTipMoving`, `_lineEndpointMoving`, `_panning`, `_tableSelecting`, …) branched through mousedown/move/up/key. Each new gesture adds a flag and more branches. One `enum InteractionState` + small handler objects would be more robust. | **incremental** |
| 5 | **`RenderedPage` LOH churn.** Every render is `new byte[stride*height]` — 60 MB+ at high zoom, straight to the LOH, GC'd; no pooling. Rent from `ArrayPool<byte>.Shared`, render, copy into the frozen `BitmapSource`, return. Better long-session stability. | **quick win, do next** |

## Not worth it

- **A GPU / Direct2D rendering pipeline.** PDFium renders to a CPU buffer; the current
  raw-BGRA → frozen `BitmapSource` path is now memory-bounded (128 MB cache, oversized renders
  bypass it, bitmaps freed on scroll-out). A rewrite buys little.
- **A different PDF library for annotations.** PDFium is the right renderer; the overlay model
  already works.
- **Per-document process isolation.** Desktop scale (1–3 docs) does not need it; it would
  multiply the interop surface.

## Notes

- **`PDFiumCore` is pinned at 4688.** Newer PDFium builds carry more of the struct-tree element
  and text-style APIs; worth a look if tagged-PDF or style-aware extraction is revisited.
- **Rendering memory** is now bounded and documented in `CLAUDE.md` ("Page rendering"): 128 MB
  raw-BGRA LRU, oversized-render cache bypass, `PageSlotViewModel.Image` freed outside the
  realised window + `ImageKeepMargin`, thumbnail requests prioritised in the queue.
