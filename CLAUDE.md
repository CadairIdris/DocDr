# DocDr — notes for Claude

PDF viewer/editor/cataloguer for Windows. WPF + PDFium. Staged build — spec in
`_specs/001-pdf-desktop-app-spec.md`, current stage progress in `README.md`.

## Conventions

- `net9.0` (`net9.0-windows` for the WPF app). Nullable + implicit usings on;
  warnings are errors (`Directory.Build.props`).
- MVVM with `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`).
- File-scoped namespaces, `_camelCase` private fields, 4-space indent (`.editorconfig`).

## Hard rules

- **All PDFium access is serialised through `PdfiumLibrary.SyncRoot`** (via `PdfDocument.Locked`).
  The `pdfium-binaries` build is not thread-safe across documents. Never bypass this.
- `DocDr.Pdf` stays WPF-free — it returns raw BGRA buffers (`RenderedPage`) and plain geometry
  structs. Bitmap/`ImageSource` creation lives in `DocDr.App` (`PageImageService`).
- The SQLite catalog (Stage 5) is the source of truth for user metadata, not the PDF itself.

## PDFiumCore API shape

Generated flat bindings. Static classes `fpdfview`, `fpdf_text`, `fpdf_doc`, `fpdf_edit`,
`fpdf_ppo`, `fpdf_annot`, `fpdf_save`. Names mostly mirror the C API but are inconsistent
(`FPDF_LoadPage` vs `FPDFTextLoadPage`). Handle types (`FpdfDocumentT` etc.) are CppSharp
wrapper classes — check `handle is null || handle.__Instance == IntPtr.Zero`; close with the
explicit `FPDF_Close*` functions, don't dispose the wrapper.

## Page editing (`PdfDocument`, Stage 2)

- **`FPDF_ImportPages` / `FPDF_ImportPagesByIndex` fail (return 0) when the destination is a
  `FPDF_LoadMemDocument` document** in this build — only a fresh `FPDF_CreateNewDocument`
  works as a dest. So a `PdfDocument` is modelled as an ordered `List<PageRef>` (source id +
  page index + rotation) plus its loaded source docs; structural edits **rebuild** the live
  handle with `FPDF_CreateNewDocument` + `FPDF_ImportPages` from the sources. Rotation and
  `FPDFPageDelete` do work in place. Rotation carries through `FPDF_ImportPages`.
- Undo/redo = snapshots of the `List<PageRef>`. `FPDF_SaveAsCopy` needs a `FPDF_FILEWRITE_`
  with `Version = 1` and a `WriteBlock` delegate; keep the delegate alive across the call.
- **PDFium has no metadata setter.** `PdfMetadataWriter.AppendInfo` appends an incremental-update
  Info object to the saved bytes (PDFium always emits a classic `xref` table + `trailer`).
  `SaveToBytes` does this when metadata was edited *or* the handle was rebuilt (rebuild loses the
  Info dict). Metadata edits are not on the undo stack.

## Watermark removal (`PdfWatermarkStripper`, Stage 4)

- **`FPDFPageRemoveObject` + `FPDFPageGenerateContent` re-serialises the whole page from PDFium's
  object model and drops the `TJ` positioning arrays** — kerned/tabular text comes out scrambled.
  So `PdfWatermarkStripper.Strip` works on the `SaveToBytes()` bytes directly: tokenises each page
  content stream, deletes only the watermark `Tj`/`TJ`/`'`/`"` operators (matched by normalised
  text against `PdfWatermarks` signatures) and watermark-image `Do` operators, drops any content
  stream left holding nothing but watermark content, re-Flate-encodes the touched streams, and
  appends one incremental update. Everything else stays byte-for-byte.
- `PdfDocument.RemoveWatermarks` runs that, then `AdoptStrippedBytes` reloads: new
  `FPDF_LoadMemDocument64`, every old source closed, `_pages` reset to identity, **undo history
  cleared** (not undoable). `_annotations` is kept (page order unchanged). Rewritten stream objects
  need `/Filter /FlateDecode`, not a bare `/FlateDecode`.

## Annotations (`PdfDocument`, Stage 3)

- Highlights, notes (`PdfAnnotationKind.Comment`, PDF `/Text` subtype — "Note" in the UI) and
  freehand ink (highlighter) live in DocDr's model — `_annotations` (a `List<List<PdfAnnotation>>`
  index-aligned with `_pages`), snapshotted alongside `_pages` in each `HistoryStep`. Quads /
  ink stroke points are in **unrotated** page space (same as `FPDFText_GetRect`).
- The **note pin** is drawn as a fixed ~26px coloured badge at the marker spot; it's draggable
  (`TryHitShapeBox` also matches `Comment`, `MoveShape` offsets `Quads[0]`), click-to-select
  (accent glow), double-click to edit. The small edit-glyph `ShowMarker` button is now only for
  a *noted highlight*.
- Ink = PDF subtype 15. `PdfInkInterop` marshals the `FS_POINTF[]` for
  `FPDFAnnotAddInkStroke` / `FPDFAnnotGetInkListPath` by hand — PDFiumCore only exposes a
  single-`FS_POINTF_` wrapper and its pointer factory (`__CreateInstance`) is `internal`, so
  it's reached once by reflection. `PdfAnnotationWriter` writes ink at ~150/255 alpha;
  `PdfCoordinates.PageToDevicePoint` is the point-level rotation map for the overlay `Polyline`s.
- **The live PDFium handle never carries our annotations during a session.** The ctor reads
  existing subtype-1/9 annotations via `PdfAnnotations.ReadLocked` then `StripManaged`s them off
  the original handle (== `_sources[0]`), so `Rebuild()` imports a clean base and the WPF overlay
  is the only thing drawing them. `SaveToBytes` bakes them onto the pages via
  `PdfAnnotationWriter.Write` around `FPDF_SaveAsCopy`, then strips them again (idempotent).
- Other annotation subtypes (ink, stamps, widgets…) are left untouched and still render.
- Metadata: `PdfAnnotation` carries `Author` / `Created` / `Modified`. Reader pulls `/T`,
  `/CreationDate`, `/M`, and a GUID-shaped `/NM` (kept as the id so identity round-trips);
  writer emits all four. New annotations stamp `Environment.UserName` + now; an edit keeps
  `/CreationDate` and only bumps `/M`.
- **Reply threads:** `PdfAnnotation.Replies` (a `List<PdfReply>`; each has its own id / text /
  author / dates). PDFium has no setter for an indirect-ref dict entry, so there's no standard
  `/IRT` chain — `PdfAnnotationWriter` serialises the whole thread to JSON via `PdfReplyCodec`
  and stashes it in the parent annotation's private `/DocDrThread` string key; the reader
  decodes it back. Round-trips through PDFium and any reader that keeps unknown keys; other
  readers still see the opening comment. Replies are part of the immutable `PdfAnnotation`
  record, so they snapshot with history and are undoable like any other annotation edit. The
  editor (`AnnotationEditorViewModel`) shows replies as a list with a reply box; the nav-panel
  row shows a reply count.
- **Stamp-backed shapes** (`PdfAnnotationKind.TextBox` / `Callout` / `Cloud`): PDFium has no
  setter for `/CL`, `/Vertices` or `/BE`, so these are written as **`Stamp` (subtype 13)**
  annotations whose appearance `PdfStampAppearance.Build` assembles from real page objects —
  `FPDFPageObjCreateNewPath` for the box/arrow/cloud scallops, `FPDFPageObjCreateTextObj` +
  `FPDFTextLoadStandardFont("Helvetica")` for the label — then `FPDFAnnotAppendObject`. PDFium
  wires up the font resources, so they render in any viewer and in the print path. The real
  geometry (box rect, callout leader, font size) round-trips through DocDr only, as JSON in the
  private `/DocDrShape` key (`PdfShapeCodec`); `StripManaged` / the reader key off subtype 13 +
  that key so *other* apps' stamps are left alone. `PdfAnnotation.Box` / `.Leader` expose the
  geometry (stored in `Quads[0]` / `Strokes[0]`). The overlay draws its own WPF version
  (`ShapeGeometry.Cloud` mirrors the PDF scallop maths); the editor reuses `AnnotationEditorWindow`
  (colour + font size). Text box / cloud creation = a `ShapeTool` armed from the toolbar
  (`DocumentTabViewModel.ShapeTool`, mirrored to panes) then drag a rectangle; a callout drags
  tip→box and `BoxAttachPoint` picks the leader's box-edge attach point.
- Text box / callout boxes **shrink-wrap to their text** while `PdfAnnotation.AutoSize` is set
  (the default; cleared once the user resizes): `PdfTextWrap` (Helvetica AFM width table, shared
  by `PdfStampAppearance` so the overlay, the `/AP` and the box size stay in step — with a small
  slack factor so the system-font overlay doesn't clip) → `PdfPaneViewModel.FitTextBox` on
  create and (if `AutoSize`) edit; `GrowToFitText` only grows a hand-sized box on edit.
- A selected box is **draggable and resizable**, and a callout's **arrow tip is draggable** —
  `PdfPaneView` routes a mousedown through `TryHitLeaderTip` → `Begin/Preview/EndLeaderTipMove`
  (`MoveLeaderTip`: box stays, leader re-attaches), else `TryHitResizeHandle` (corner handles,
  `BoxHandle`) → `Begin/Preview/EndShapeResize`, else `TryHitShapeBox` → `Begin/Preview/EndShapeMove`.
  The live geometry is applied in `BuildAnnotationOverlays` (`MoveShape` / `ResizeShape` /
  `MoveLeaderTip`), not committed, until the drop's one `UpdateAnnotation`. Double-click a box to
  edit, `Esc` cancels. `AnnotationVisual.ResizeHandles` (squares) + `LeaderTipHandle` (a circle)
  are drawn non-interactive for the selected shape.
- **Click vs drag:** `PdfPaneView` records `_pointerDown` on mouse-down; the text-selection
  branch only commits (`EndTextSelection` + open `SelectionPopup`) if `PointerDragged` — moved ≥
  `SystemParameters.Minimum{H,V}DragDistance`. A plain click on the page clears any pending run
  and deselects the current annotation (no popup). There is no separate "select vs pan" mode.
- `AnnotationsChanged` is the light event (overlay rebuild only); `Changed` is the heavy one
  (full pane/thumbnail reload). Undo/redo raises `Changed` only when pages/rotations actually
  moved, `AnnotationsChanged` always.
- `PdfCoordinates.PageToDevice(rect, unrotatedSize, rotation, scale, cropOrigin)` / `DeviceToPage(...)`
  are the rotation-aware maps used by the overlay and mouse hit-testing (also fixes search
  highlights on rotated pages). `PdfDocument.GetUnrotatedPageSize` swaps W/H for 90/270 —
  `FPDF_GetPageSizeByIndex` in this build returns the *rotated* size.
- **CropBox ≠ MediaBox:** PDFium renders (and reports page size for) the *CropBox*, but the text
  and annotation APIs (`FPDFText_GetCharBox`/`GetRect`, `FPDFAnnotGetRect`, `FPDFLinkGetDest` rects,
  ink points) return coords in *MediaBox* space. DocDr's model stays MediaBox-relative; the App's
  device↔page transforms take a `cropOrigin` (`PdfDocument.GetCropOrigin(i)` = CropBox lower-left
  minus MediaBox lower-left, clamped ≥ 0) and subtract it going page→device / add it going back.
  Every `PageToDevice*` / `DeviceToPage` call site in `PdfPaneViewModel` passes it.
  `_cropOrigins` is cached alongside `_pageSizes` and cleared with it.

## Printing (`PrintService`, Stage 1)

- `DocumentTabViewModel.PrintCommand` → `PrintService.Print(document, jobName)`: a WPF
  `PrintDialog` (`UserPageRangeEnabled`), then a `PdfPagePaginator` (a `DocumentPaginator`)
  handed to `dialog.PrintDocument`. `GetPage(i)` rasterises one page via `PageImageService`
  (`PageRenderer`, **not** the tab's cache) at 200 DPI, fits it to `PrintableArea*` preserving
  aspect, and `DrawImage`s it centred into a `DrawingVisual`. Raster output.
- Prints the **current in-memory state**: the render is wrapped in
  `PdfDocument.WithAnnotationsBaked(...)` — the same `BakeAnnotations` / `UnbakeAnnotations`
  dance `SaveToBytes` uses, so highlights / notes / ink print (they are normally overlay-only
  and absent from the live handle). The doc lock is re-entrant, so the nested `Render` → `Locked`
  is fine. Page edits/rotations are already on the handle.
- `PrintDocument` blocks the UI thread for the whole spool; a range over ~40 pages warns first.

## Design-code navigation (`PdfClauses` / `PdfCrossReferences`, Stage 8)

- **`PdfClauses.ReadStructure(doc, ct)`** (`DocDr.Pdf`) → `PdfCodeStructure(Clauses, Captions)`.
  One pass over `PdfTextExtractor.GetPageText` (clean reading-order text — *not* per-char
  reconstruction) detects both clause headings and figure/table caption lines. `PdfClauses.Read`
  is a thin wrapper returning just `.Clauses`. Heuristic, tuned for **dotted-decimal** numbering
  (Eurocode / BS EN / ISO): `[GeneratedRegex]` for `6.4.3`-style, `Annex X` / `A.2.1`, and bare
  chapters (`7 Structural analysis` — gated by `LooksLikeChapterTitle`); `CaptionLine` matches
  `^(Figure|Table) <num> <dash/colon>` → `Captions["Figure 8.5"] = page` (first sighting wins).
  A whole page is skipped as a contents page when ≥ 5 of its lines hit `TocLeader` (dotted
  leader). `BuildTree` synthesises missing prefix ancestors, fills a title-less node only from a
  sighting at/near its first subclause's page, orders siblings numerically. **AISC-style letter
  sections (`D1.2a`) are NOT covered.** ~5 s for a 400-page Eurocode → App runs it on a
  background thread.
- App: `NavigationTab.Clauses`, `ClausesViewModel` (background `Task.Run`, continuation on
  `FromCurrentSynchronizationContext`, `Scanned` event, `CancelLoad` on dispose),
  `ClausesPanelView` (TreeView, `SelectedItemChanged` → jump, right-click → `CopyCitationCommand`).
  Reload on `Document.Changed`.
- **`PdfCrossReferences.Scan(doc, page, pageMap)`** finds *cued* clause refs
  (`see|in accordance with|according to|… 8.3.1`), `Annex L` refs, and `Figure 8.5` / `Table 4.3`
  refs in the page's char boxes, maps the match's string offsets back to a union `PdfRect`, and
  resolves against `pageMap`. Clause numbers walk up the dotted prefix; figure/table keys
  (`"Figure 8.5"`) are **exact match only** (no wrong jumps — `Figure 8.5` ≠ clause 8.5).
  `BuildPageMap(PdfCodeStructure)` merges the flattened clause tree with the caption map.
  Formula refs are not handled. Same-page targets are skipped (the caption line doesn't link to
  itself).
- Wiring: `DocumentTabViewModel.OnClausesScanned` → `PdfPaneViewModel.SetClausePageMap` on both
  panes → clears `_crossRefCache`, `BuildLinkOverlays` merges cross-refs into the Stage 1 link
  overlay as `LinkVisual`s with a `Label` (`IsCrossReference` → faint underline in the template).
  `FollowLinkCommand` handles them (same `GoToPage` path as real `/Link`s).
- **Cite this** — `DocDr.App/Services/Citations.cs` (`Format` for a passage, `ForClause` for a
  clause, `CopyToClipboard`). Clause tree context menu, and a "Cite" button in the text-selection
  popup (`PdfPaneViewModel.CiteSelectionCommand` uses `_pendingText` + `CurrentClauseNumber(page)`
  = deepest clause whose page ≤ the selection's). Source name is the filename stem for now
  (catalog metadata once Stage 5 lands).

## RAG chunk export (`PdfRagChunker`, Stage 5)

- **`PdfRagChunker.Chunk(doc, options, ct)`** → `RagChunkResult(Chunks, PagesWithoutText, SectionCount)`.
  One pass caches every page's `GetPageText`; sections come from `PdfClauses.Read` (if ≥ 3
  nodes) → `PdfBookmarks.Read` (if ≥ 3) → a single "Front matter" section, filled forward so
  every page has one `section_title`.
- Cleaning per page: skip a whole page if ≥ 5 lines hit `TocLeader` (contents page); drop
  lines that are boilerplate (normalised line recurring on ≥ 50% of pages — **no length cap**,
  a long legal footer PDFium extracts as one run still counts), bare page numbers, or dotted
  leaders; de-hyphenate `word-\nword`; join wrapped lines into sentence-ish paragraphs
  (`BlockStart` / sentence-enders / `LooksLikeHeading` start a new one).
- Windowing: `MaxChars = clamp(TargetTokens, 48, 4000) * 4` (default 600 tok), `budget =
  MaxChars − OverlapChars`. Paragraphs are flattened to units ≤ budget (sentence-split then
  hard-sliced), then packed: emit before a unit would push the buffer past `MaxChars`, then
  seed the next buffer with a word-snapped char tail of `OverlapChars`. **Every chunk ends up
  ≤ MaxChars** — the earlier paragraph-level carry could stack to ~2×, this can't.
- `RagChunk` serialises snake_case (`source_path`, `page_start`, …) via
  `JsonNamingPolicy.SnakeCaseLower` + `UnsafeRelaxedJsonEscaping`. `WriteJsonl(chunks, path)`
  is UTF-8 no-BOM, one object per line.
- App: `DocumentTabViewModel.ExportRagChunksCommand` (async — `Task.Run(Chunk + WriteJsonl)`),
  toolbar "Export chunks…". Batch/folder mode and catalog persistence are not built yet.

## Read mode (Stage 1)

- `MainViewModel.IsReadMode` — full-screen (`WindowStyle=None` + borderless maximise, done in
  `MainWindow.xaml.cs`), all chrome hidden (toolbars / status bar / tab strip / pane toolbar
  bound to it; nav panel + split view forced off and restored). Bound to one tab
  (`_readingTab`); switching tabs or `Esc`/`F11` exits and restores the prior `ViewMode`.
- `ViewMode.TwoPage` — one spread at a time (pages `2s`, `2s+1`), `CurrentPage` snapped to the
  spread's left page. `IsPaged` (`SinglePage or TwoPage`) gates the "one screenful, no scroll
  sync" paths. `ApplyTwoPageLayout` fits the pair to the viewport (no `Zoom`). The page list
  swaps to a horizontal `SpreadPanel` **and `ScrollViewer.CanContentScroll=false`** — an
  item-scrolling `ScrollViewer` over a non-`IScrollInfo` panel reports its viewport in "items"
  and the fit maths collapse. Wheel / arrows / space call `PdfPaneViewModel.Advance(±1)`.

## In-document links (`PdfLinks`, Stage 1)

- `PdfLinks.Read` walks a page's subtype-2 (`/Link`) annotations: `FPDFAnnotGetRect` for the
  region, `FPDFAnnotGetLink` → `FPDFLinkGetDest` / `FPDFLinkGetAction` (GoTo → `FPDFActionGetDest`,
  URI → `FPDFActionGetURIPath`) → `FPDFDestGetDestPageIndex`. Same dest/action dance as
  `PdfBookmarks`. Rects are **unrotated** page space.
- `PdfWatermarks.Scan` counts them as body content (a page with links isn't blanked by a
  watermark strip). Link annotations do **not** survive `FPDF_ImportPages` — after a page edit
  the overlay is gone until reload; acceptable (viewing, not authoring).
- App: `PdfPaneViewModel._linkCache` (raw links per page, cleared on `ReloadPages`),
  `BuildLinkOverlays` projects them for realised slots only (called from `UpdateVisibleRange`
  / layout). Overlay = transparent `Button`s (so a click follows the link, not starts a
  selection — `PageList_MouseLeftButtonDown` already skips `ButtonBase`). `FollowLinkCommand`
  → `GoToPage`, or `Process.Start` for `http`/`https`/`mailto`.

## Testing

`dotnet test`. Fixtures are generated in-process by `TestPdfBuilder` (a minimal PDF writer) —
no binary files in the repo. Test parallelism is disabled (native interop). `PdfAnnotations.Read`
re-reads from the live handle, which the `PdfDocument` ctor has already stripped — so tests must
assert with `doc.GetAnnotations(i)` (the model), not `PdfAnnotations.Read` after a `Load`.

Set `DOCDR_BINDING_LOG=<path>` to have the app log WPF data-binding errors to that file
(`App.OnStartup`). Gotcha: a child view that sets its own `DataContext` (e.g. `PdfPaneView
DataContext="{Binding RightPane}"`) resolves its *other* bindings against that new context —
use `RelativeSource AncestorType=...` to reach the parent VM.

Gotcha: a modal `Window.ShowDialog()` raised **synchronously from a mouse-down / popup-click
handler** opens without activating (invisible-ish, not in the UIAutomation tree). Post it with
`Dispatcher.BeginInvoke(..., DispatcherPriority.Input)` so it runs after the input event
unwinds (see `PdfPaneViewModel.OpenEditor`).

Gotcha: `RadioButton.GroupName` is **not scoped to a view instance**. `PdfPaneView` is
instantiated twice (left + right pane), so a shared `GroupName="Mode"` grouped all six
buttons — the hidden right pane's `IsChecked` binding stole the check from the visible left
pane at startup, leaving nothing selected. Fix: no `GroupName` (RadioButtons then group by
their common parent panel, which is per-pane) and drive the VM from the `Checked` event with
a `OneWay` `IsChecked` binding back to state.

## Theming

`ThemeService` sets .NET 9's `Application.ThemeMode` (Fluent light/dark for built-in controls;
API is `[Experimental("WPF0001")]` — suppressed) and swaps `Themes/Palette.{Light,Dark}.xaml`
for DocDr's own tokens. **All palette brushes must be referenced as `DynamicResource`** (not
`StaticResource`) so a theme swap takes effect. **A `<Style TargetType="X">` with no `BasedOn`
replaces the Fluent theme style for that control** — always
`BasedOn="{StaticResource {x:Type X}}"`. Fluent's default Button padding clips a fixed-width
icon button (see `GlyphButton` in `PdfPaneView.xaml`).
