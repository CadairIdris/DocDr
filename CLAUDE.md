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
- **Encrypted sources** (BSI "licensed copy" PDFs are `/V 1 /R 2` RC4): `SerialiseCurrentHandle`
  passes `FPDF_SaveAsCopy` flag **3 (`FPDF_REMOVE_SECURITY`)** when
  `FPDF_GetSecurityHandlerRevision(Handle) >= 0`, else 2 (`FPDF_NO_INCREMENTAL`). Flag 3 is
  matched *exactly* by PDFium (`== FPDF_REMOVE_SECURITY`) and forces a full non-incremental
  rewrite with no encryption — without it the stripper sees ciphertext and matches nothing.
  So Save / SaveAs / strip on a secured PDF all drop the security handler; `PdfDocument.IsEncrypted`
  exposes the state and the watermark review dialog notes it.

## Navigation panel (icon rail)

- **`NavigationRailView`** is a ~40px always-visible strip (column 0 of `DocumentTabView`) with
  four icon buttons — Pages / Bookmarks / Clauses / Comments. Each runs
  `DocumentTabViewModel.ShowNavigationSectionCommand(NavigationTab)`: opens the panel to that
  section, or collapses it if it's already open on that section. Active-section highlight (accent
  bar) via `NavRailActiveConverter` (values `[NavigationTab, IsNavigationPanelVisible]`, param =
  member name) bound to each button's `Tag`, with a `<sys:Boolean>True` trigger value (a string
  `"True"` won't match a boxed bool on an `object` `Tag`).
- **`NavigationPanelView`** is now just the four section `ContentControl`s (no internal tab
  strip). The section icon geometries + `NavTabIcon` `Path` style moved to `App.xaml`.
- Rail hides in read mode (bound to the window's `IsReadMode`, like the tab strip). There is no
  toolbar "Navigator" button any more. Panel still starts closed; `DocumentTabView.xaml.cs`
  `ApplyNavColumn` (collapse/restore the panel column) is unchanged — the panel is now column 1.

## Batch merge + generated outlines (`PdfDocument.Merge` / `PdfOutlineWriter` / `PdfHeadings`)

- **`PdfDocument.Merge(paths)` → `MergeResult(Document, SourceStartPages)`** — a new untitled
  (`FilePath == null`), dirty document built in ONE pass: `FPDF_CreateNewDocument` +
  `FPDF_ImportPages(merged, src, null, dest)` per source, then `DetachSource` each into
  `_sources` so later structural edits still `Rebuild()` from them. `SourceStartPages[i]` is the
  0-based page each input's pages begin at. A dedicated private ctor sets `_pages`/`_annotations`/
  `_sources` directly (no per-page annotation read — sources were already stripped by their own
  `Load`).
- **Outline model**: `PdfDocument._outline` (`IReadOnlyList<PdfBookmark>?`). `GetOutline()` returns
  it if set else `PdfBookmarks.Read(this)`; **the App uses `Document.GetOutline()`, not
  `PdfBookmarks.Read`, everywhere**. `SetOutline(tree)` sets it, `SetDirty`, raises the dedicated
  **`OutlineChanged`** event (lighter than `Changed` — only the bookmarks panel refreshes);
  **not on the undo stack** (like metadata). A structural edit after `SetOutline` leaves the
  model outline in place but its page indices go stale — regenerate.
- **Rename / delete** (bookmarks panel, right-click / F2 / Del): `BookmarkNodeViewModel.Title` is
  an `[ObservableProperty]`, `ToBookmark()` rebuilds a `PdfBookmark`; `BookmarksViewModel` raises
  `OutlineEdited(newTree)` → `DocumentTabViewModel.OnBookmarksEdited` calls `Document.SetOutline`
  behind an `_applyingOutlineEdit` guard so `OnOutlineChanged` skips the panel reload (the VM
  already holds the edited tree). Inline edit = a `TextBox` toggled by `IsEditing`, committed on
  `LostFocus`/Enter, reverted on Esc (`BookmarksPanelView` code-behind).
- **`PdfOutlineWriter.Append(bytes, outline)`** — called only from `SaveToBytes` (not
  `SerialiseCurrentHandle`, which also feeds the watermark/OCR adopt path). Incremental-update
  append modelled on `PdfMetadataWriter` (which now exposes `internal PdfString`): reads the last
  `trailer` for `/Root` + `/Size`, bracket-scans the Catalog dict, walks `/Pages` → `/Kids` (flat
  or nested) for page object refs, emits one object per bookmark (`/Title /Parent /Prev /Next
  /First /Last /Count /Dest [<pageObj> 0 R /Fit]`) + an `/Outlines` dict + a Catalog override
  with `/Outlines` spliced in, then a two-subsection `xref` + `trailer /Prev`. Unparseable input
  → returns the bytes unchanged (outline stays session-only).
- **`PdfHeadings`**: `FromHeadings(doc)` maps the whole `PdfClauses.ReadStructure` tree to
  `PdfBookmark`s (≥ 2 nodes or `[]`); `SubHeadings(doc)` = a single doc's numbered sub-headings
  (dotted numbers only, doc-local pages, sentence-ish lines dropped); `Shift(tree, delta)` offsets
  every page index; `SuggestTitle(doc, path)` = own first outline entry → first substantial page-1
  line → cleaned filename.
- App: `MainViewModel.MergeCommand` (multi-select `OpenFileDialog` → `MergeWindow` /
  `MergeViewModel` — reorderable `DataGrid`, editable title per file, "Add a bookmark for each
  file" checkbox) → `Task.Run(Merge + per-file SetOutline)` → `AddDocumentTab` (the
  path-less half of `OpenPathAsync`). `DocumentTabViewModel.GenerateBookmarksCommand` +
  `CanGenerateBookmarks` (`!Bookmarks.HasBookmarks`) for the "Generate from headings" button in
  `BookmarksPanelView` (reaches the tab VM via `RelativeSource AncestorType=NavigationPanelView`).
- **Cross-references are never persisted** — `PdfClauses`/`PdfCrossReferences` run on every open
  (`ClausesViewModel` background scan → `_crossRefCache`, in-memory only). A merged+saved file
  re-derives them next open.

## Collage authoring (new documents, paste, basic shapes)

- **`PdfDocument.CreateBlank(PdfSize sizePoints)`** — one `FPDF_CreateNewDocument` +
  `FPDFPageNew(doc, 0, w, h)` + `FPDFPageGenerateContent`, through the same private ctor as
  `Merge` (`FilePath == null`, `IsDirty`, `_handleHasOriginalInfo = false`). App:
  `MainViewModel.NewDocumentCommand` → `NewDocumentWindow` / `NewDocumentViewModel` (size combo
  A4/A3/A2/A1/Custom, mm W/H editable for Custom and pre-filled from the last standard pick via
  `AppSettings.LastPageSize` etc., Landscape checkbox) → `PageSizes.FromMm` (mm × 72/25.4) →
  `AddDocumentTab(doc, "Untitled")`. Home-screen "New document…" + toolbar "New…".
- **Paste** (`PdfPaneView` `Ctrl+V` → `PasteAtVisibleCentre` → `PdfPaneViewModel.PasteFromClipboard`):
  clipboard image (or a dropped image file) → `PdfAnnotation.NewImage` (px→pt 1:1, capped at 85 %
  of the page, box centred on the visible-area centre); else clipboard text → `PdfAnnotation.NewTextBox`.
  The new annotation is selected so it can be dragged immediately.
- **Image annotations** (`PdfAnnotationKind.Image`, stamp-backed): bytes live in
  `PdfAnnotation.ImageData` (shared by reference across undo snapshots — a move only makes a new
  record). `DocDr.Pdf` stays WPF-free via **`IImageDecoder`** (`DecodeToBgra(byte[]) → (w,h,stride,bgra)`);
  `AppImageDecoder` (WPF `BitmapDecoder` + `FormatConvertedBitmap` to Bgra32) is set on
  `PdfDocument.ImageDecoder` in `MainViewModel.AddDocumentTab`. `PdfStampAppearance.DrawImage`
  bakes a real `FPDFPageObjNewImageObj` + `FPDFImageObjSetBitmap` + `FPDFImageObjSetMatrix`
  (PDFium image space is unit-square × the matrix, bottom-left origin). Exact bytes round-trip
  through the private `/DocDrImage` base64 key; the appearance renders in any viewer.
- **Basic shapes** — `Rectangle`, `Ellipse`, `Line`, `Arrow` (`ShapeTool` + `PdfAnnotationKind`,
  toolbar toggles after Cloud), all stamp-backed. Rectangle/Ellipse carry the box in `Quads[0]`;
  Line/Arrow keep `[tip, tail]` in `Strokes[0]` and expose it as `Leader` (reuses the callout
  leader plumbing — `MoveShape` shifts both endpoints together, `TryHitShapeBox` does a
  segment-distance test). `PdfStampAppearance`: `Ellipse` = 4-bezier path (`kappa`), `Line`/`Arrow`
  = `DrawLeader(..., arrow:)`.
- **Shape colour round-trips via `/DocDrShape`, not `FPDFAnnotGetColor`.** `FPDFAnnotGetColor`
  fails on a reloaded stamp that carries an appearance stream, and `ReadColor` then falls back to
  yellow (`0xFFFFD54F`). `PdfShapeCodec.Shape` carries a `uint? Color` (null when 0); `Decode`
  uses `shape.Color ?? colorArgb`.

## Per-annotation format toolbar (`AnnotationFormatViewModel`)

- **Selecting a shape / line / text box / cloud / highlight / note raises a floating format bar**
  (`PdfPaneView.xaml` `FormatBarPopup`, one per pane, `Placement=Relative` to `PageList`,
  positioned by `PdfPaneView.xaml.cs` `PositionFormatBar` — reuses `FindDescendantSlot` +
  `TransformToVisual` like the paste/link overlays; repositions on `ScrollChanged` + a guarded
  `LayoutUpdated`). Ink / image get no bar.
- `PdfPaneViewModel.SelectedFormat` (`AnnotationFormatViewModel?`) is rebuilt at the end of every
  `BuildAnnotationOverlays` from the selected annotation (`RefreshSelectedFormat`, cleared while a
  drag is in progress); `SelectedFormatChanged` tells the view to reposition. `SelectedFormatPage`
  + `SelectedFormatAnchor` (top-centre of the annotation's device bounds) place the bar.
- The bar's two-way props / commands call `PdfPaneViewModel.ApplyFormat(id, a => a with {…})`
  → `UpdateAnnotation` (so every style change is undoable). `ShowStrokeColor / ShowLineWidth /
  ShowDashed / ShowFill / ShowTextColor / ShowBorderToggle / ShowFontSize` gate the controls by
  kind. Font-size steps go through `ResizeTextBoxForFont` (re-fits the box + re-attaches a callout leader).
- **Styling fields on `PdfAnnotation`** (all default to "off", round-trip in `/DocDrShape` via
  new nullable `Shape` members): `LineWidth` (0 → `DefaultLineWidth` 1.5; `EffectiveLineWidth`),
  `FillArgb` (rect/ellipse tint, alpha `0x40` by convention), `Dashed`, `Borderless`
  (text box / callout — skip the box stroke), `TextColorArgb` (text box / callout text; null = black).
- **`FPDFPageObjSetDashArray` does NOT render on a stamp-appearance path in this PDFium build** —
  `PdfStampAppearance.DashPolyline` draws the gaps geometrically (rect → 5-pt polyline, ellipse →
  96-pt sample, line → its 2 points; arrowhead stays solid). `StrokePath(…, bool stroke, uint? fill)`
  does fill (`FPDFPathSetDrawMode` winding) ± stroke.
- **Line / arrow end points are draggable** when selected: `AnnotationVisual.EndpointHandles` (two
  circles), `PdfPaneViewModel.TryHitLineEndpoint` → `Begin/Preview/End/CancelLineEndpointMove` +
  `MoveLineEndpoint` (mirrors the callout `_leaderTipId` plumbing; wired in `PdfPaneView.xaml.cs`
  before `TryHitLeaderTip`). `MoveShape` still moves the whole line.
- **Custom colour**: `AnnotationColors.Keys` = the 5 presets + `"Custom"`; `PresetKeys` excludes it.
  `AnnotationColors.CustomColorArgb` (static, app-wide) is seeded from `AppSettings.LastCustomColor`
  in `MainViewModel` and updated by `DocumentTabViewModel.ApplyCustomColor` (which refreshes the
  `InkColorKeys` list so the "Custom" swatch re-renders, selects it, and raises `CustomColorChanged`
  → `MainViewModel` persists). `ColorPickerWindow` (RGB sliders + hex + presets, no WinForms) is
  the picker — reached from the toolbar rail's "Custom" swatch (`MainWindow.xaml.cs`), the format
  bar (`PdfPaneViewModel.PickCustomColor`, which yields via `Dispatcher.Invoke(Background)` first so
  the popup click unwinds), and the text-selection popup.
- **Text box / callout lost their colour + size controls in `AnnotationEditorWindow`** — that
  modal is now text-only for them (`ShowColors => IsHighlight`); everything visual is on the bar.
  `EditAnnotation` early-returns for rect/ellipse/line/arrow/cloud (no note). Nav-panel + delete-button
  labels come from `AnnotationKinds.Label`.

## OCR (`PdfOcr` / `DocDr.Ocr`, Stage 6)

- **`DocDr.Ocr`** is a separate `net9.0` (x64-only) project so the Tesseract dependency stays out
  of `DocDr.Pdf`. `IOcrEngine` / `OcrWord` / `OcrProgress` / `OcrResult` are declared in
  `DocDr.Pdf` (`PdfOcr.cs`); `TesseractOcrEngine` (the only impl) lives in `DocDr.Ocr` and wraps
  `TesseractOCR` 5.5.0 (Sicos1977 fork — bundles `leptonica`/`tesseract55` natives per-arch).
  The model (`tessdata_fast/eng`, ~4 MB) is committed at `src/DocDr.Ocr/tessdata/eng.traineddata`
  and copied next to the app. `OcrWord` boxes are in **rendered-image pixel space** (top-left
  origin). One engine instance is not thread-safe — the app makes one per run and disposes it.
- `TesseractOcrEngine.Recognise` encodes the BGRA `RenderedPage` as an in-memory 24-bit BMP
  (`BmpWriter` — Leptonica reads BMP from memory; PNG needs an encoder we don't have), then
  walks `page.Layout` → `Block` → `Paragraph` → `TextLine` → `Word`. `user_defined_dpi` is set
  to 300 so Tesseract doesn't warn/guess.
- **`PdfDocument.AddOcrTextLayer(engine, progress, ct)`** — for every page `PagesWithoutText()`
  reports (empty reading-order text), renders at `PdfOcr.RenderDpi` (300, longest edge capped
  at 4200 px), recognises, and writes each ≥ `MinConfidence` (40) word as an **invisible
  (render-mode-3) text object** horizontally scaled to its image box via the Helvetica AFM
  table (`PdfTextWrap.MeasureHelvetica`), Y-flipped and crop-origin-shifted into MediaBox page
  space. `FPDFPageGenerateContent` per page, then `SerialiseCurrentHandle` + `AdoptStrippedBytes`
  (same reload/adopt as watermark strip). **Not undoable, clears undo history.** Cancelling
  keeps the pages done so far (re-run picks up the rest); a cancel before page 1 mutates
  nothing. ~2 s/page.
- App: toolbar **OCR…** button → `DocumentTabViewModel.OcrDocumentCommand`. `MessageBox` if no
  blank pages, else a modal `OcrProgressWindow` (`OcrProgressViewModel` — determinate bar +
  page counter, Cancel). Work on `Task.Run`; `Progress<OcrProgress>` marshals the counter;
  `ContinueWith` on the UI scheduler closes the dialog and reports. `Document.Changed` drives
  the reload. `OcrProgressWindow.OnClosing` blocks Esc/X until the run signals `Finish()`.
- Because `AddOcrTextLayer` calls `SetDirty` / `Changed` from the background task,
  `DocumentTabViewModel`'s `OnDocumentChanged` / `OnDocumentDirtyChanged` / `OnAnnotationsChanged`
  hop to the dispatcher (`RunOnUi`) before touching bound commands — `RelayCommand.NotifyCanExecuteChanged`
  reads a `Button` DP and throws cross-thread otherwise.

## Annotations (`PdfDocument`, Stage 3)

- Highlights, notes (`PdfAnnotationKind.Comment`, PDF `/Text` subtype — "Note" in the UI) and
  freehand ink (highlighter) live in DocDr's model — `_annotations` (a `List<List<PdfAnnotation>>`
  index-aligned with `_pages`), snapshotted alongside `_pages` in each `HistoryStep`. Quads /
  ink stroke points are in **unrotated** page space (same as `FPDFText_GetRect`).
- The **note pin** is drawn as a fixed ~26px coloured badge at the marker spot; it's draggable
  (`TryHitShapeBox` also matches `Comment`, `MoveShape` offsets `Quads[0]`), click-to-select
  (accent glow), double-click to edit. The small edit-glyph `ShowMarker` button is now only for
  a *noted highlight*.
- **Hover preview**: the pin (and the noted-highlight button) carry a rich `ToolTip` (the
  `NoteTip` style in `PdfPaneView.xaml`) showing `NoteMeta` (author · date, from
  `PdfPaneViewModel.FormatNoteMeta`), `NoteText` and reply count — read a note without the
  editor. The pin is `IsHitTestVisible` for this; the `Preview*` mouse handlers on `PageList`
  still own select / drag / double-click. The `ToolTip` style pins its `DataContext` to
  `PlacementTarget.DataContext` (the `AnnotationVisual`).
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

## Page rendering (`BackgroundRenderQueue` / `PdfPaneViewModel`, Stage 1)

- **Panning a zoomed page:** `CanContentScroll="True"` (needed to virtualise 408 pages) makes the
  vertical `VirtualizingStackPanel` the `IScrollInfo`, and it gives no usable horizontal
  scrollbar even when `ScrollableWidth > 0`. So a page wider than the pane is panned by
  **middle-mouse drag** (`PageList_PreviewMouseDown`/`Up`, `_panning`, cursor `ScrollAll`),
  **Shift+wheel** (`PageList_PreviewMouseWheel`), or a **trackpad two-finger sideways swipe** —
  WPF has no routed event for the horizontal wheel, so `HorizontalWheelHook` traps the Win32
  `WM_MOUSEHWHEEL` on the `HwndSource` (added in `OnLoaded`, removed in `OnUnloaded`) and acts
  only when `PageList.IsMouseOver`. `HorizontalWheelScale` (1.0) carries the sign — flip it if a
  pad scrolls the wrong way. All three drive `_scrollViewer.ScrollTo*Offset`.

- One background worker rasterises pages newest-request-first. **The worker `catch`es every
  per-request exception and keeps looping** — an uncaught throw there kills the worker and
  *every* page then renders blank forever (this was a real bug: extreme zoom → a
  `new byte[stride*height]` of hundreds of MB → `OutOfMemoryException` → dead worker).
- `PdfPaneViewModel.MaxRenderEdge` (4096) caps a page bitmap's long side; WPF upscales it into
  the (larger) layout box, so only very high zoom goes soft. `CappedRenderSize` is used for both
  the enqueue size and the `OnPageRendered` stale check, so they agree.
- **Visible-range detection reads WPF's realised containers, not the app's height model.**
  `PdfPaneView.VisiblePageRange()` walks `PageList.Items` → `ContainerFromItem` → the containers
  that actually intersect the viewport (`TransformToVisual(scrollViewer)`), and `RefreshVisibleRange`
  / `TopVisiblePageIndex` use that for SinglePage + Continuous. The app's own `GetPageAtOffset`
  (a `LayoutHeight`-sum) drifts from the virtualising panel's pixel-extent *estimate* for a
  layout pass or two right after a zoom — trusting it there realised (and rendered) the wrong
  pages, leaving what was actually on screen blank. `GetPageAtOffset` is now only a fallback for
  the frame before any container is realised (and still drives Grid rows).
- `UpdateVisibleRange` clamps the realised span to `MaxRealizedPages` (16) as a backstop against
  a bad reading queuing hundreds of large renders.

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
- **Table extraction** — `DocDr.Pdf/PdfTableExtractor.Extract(doc, page, region)` → `TableGrid`
  (`.ToCsv()` RFC 4180 / `.ToTsv()`). `region` is unrotated MediaBox page space (like
  `PdfCharBox.Box`). Rows: thin ruled lines (walk `FPDFPath*` segments, apply the object matrix)
  else y-cluster the char boxes. Columns: vertical rules else the whitespace channels — a data
  row "votes" for a gap at x only when no run straddles x *and* it has a cell further right
  (ignores the ragged right edge; caption / note / spanning-header rows with one very wide run
  are dropped). `TrimEmptyEdges` cleans margin rows/cols. App: `DocumentTabViewModel.TableSelectActive`
  (mirrored to panes, mutually exclusive with the other tools), toolbar "Extract table" toggle;
  `PdfPaneView` drags a marquee (`BeginTableSelect`/`ExtendTableSelect`/`EndTableSelect`, reuses
  `PageSlotViewModel.ShapePreview`), `TableRegionSelected` → `TableExtractWindow` (a `DataView`
  over a `DataTable` in a `DataGrid`, Copy TSV / Save CSV). Nested headers are best-effort.
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
- `RagChunk` = `id` (`{filename stem}#c{index:D4}`, stable across re-exports) / `text` /
  `source_path` / `doc_title` / `page_start` / `page_end` / `section_title` / `chunk_index` /
  `token_estimate` (`text.Length / 4`). Serialised snake_case via `JsonNamingPolicy.SnakeCaseLower`
  + `UnsafeRelaxedJsonEscaping`; `WriteJsonl(chunks, path)` is UTF-8 no-BOM, one object per line.
- `RagChunkOptions.IncludeSectionHeading` (default true) prefixes `text` with
  "`{doc title} — {section}\n\n`" (contextual-retrieval-lite). The windower reserves 160 chars
  for it (`maxChars = MaxChars − 160`) so the finished text still lands ≤ `MaxChars`.
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
