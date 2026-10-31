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
- `DocDr.Pdf` stays WPF-free — it returns raw BGRx buffers (`RenderedPage`, or `PageRenderer.RenderInto`
  caller-owned memory) and plain geometry
  structs. Bitmap/`ImageSource` creation lives in `DocDr.App` (`PageImageService`).
- The SQLite catalog (Stage 5) is the source of truth for user metadata, not the PDF itself.

## PDFiumCore API shape

Generated flat bindings. Static classes `fpdfview`, `fpdf_text`, `fpdf_doc`, `fpdf_edit`,
`fpdf_ppo`, `fpdf_annot`, `fpdf_save`. Names mostly mirror the C API but are inconsistent
(`FPDF_LoadPage` vs `FPDFTextLoadPage`). Handle types (`FpdfDocumentT` etc.) are CppSharp
wrapper classes — check `handle is null || handle.__Instance == IntPtr.Zero`; close with the
explicit `FPDF_Close*` functions, don't dispose the wrapper.

## PDFium native build (bindings 4688, binary current)

- **The bindings and the native library come from different packages.** `DocDr.Pdf.csproj` takes
  `PDFiumCore` 4688.0.0 (its last release) with **`ExcludeAssets="native"`**, and the binary from
  bblanchon's `bblanchon.PDFium.Win32` / `.Linux` (158.0.8125 = chromium/8125). Linux is there so
  `dotnet test` runs in a Linux container; `ExcludeAssets` stops PDFiumCore's old `pdfium.dll`
  reaching `DocDr.App` too (check `runtimes/win-x64/native/pdfium.dll` is the bblanchon one).
- Why: the old 4688 binary drops some ToUnicode entries — Word's maths font's
  `<0747> <D835DC58D835DC58>` came out as the raw CID (Syriac/Oriya/Ethiopic letters). See the RAG
  section. Annex O sample: 190 → 21 garbage chars in chunk export (the 21 are U+FFFD the font itself
  maps), "(O.5)" no longer lost. A plain non-embedded synthetic font with the same doubled entry
  extracts fine on *both* builds, so there's no fixture test for it — the failure needs the embedded
  TrueType font.
- **ABI check when bumping the binary:** diff `nm -D` exports against the old one. 8125 removed only
  `FPDFFont_GetFontName` and `FPDFText_GetTextRenderMode` (DocDr calls neither); `FPDFBitmap_FillRect`
  now returns `FPDF_BOOL` instead of `void` (harmless). Any new function the bindings don't
  declare needs a hand-written `[DllImport("pdfium")]`.
- Re-checked quirks on 8125: `FPDF_GetPageSizeByIndex` still returns the *rotated* size.
  `FPDF_ImportPages` into a `FPDF_LoadMemDocument` destination **now works**, but the rebuild-from-
  sources model below stays — it's what undo is built on.

## Page editing (`PdfDocument`, Stage 2)

- **`FPDF_ImportPages` / `FPDF_ImportPagesByIndex` failed (returned 0) when the destination was a
  `FPDF_LoadMemDocument` document** in the old 4688 build (works on 8125, design kept) — only a fresh `FPDF_CreateNewDocument`
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

## Extract pages (`PdfDocument.ExtractPages` / `PdfPageRange`)

- **`PdfDocument.ExtractPages(pages, PdfExtractOptions)` → `byte[]`** (`PdfDocument.Extract.cs` — `PdfDocument`
  is now `partial`): `FPDF_CreateNewDocument` + `FPDF_ImportPages(dest, Handle, "1,3-5", 0)` from the
  **live handle** (edits / rotations / crops included; the source isn't touched), serialised with a
  private `SaveHandle` (flag 2). The result is **never encrypted** — PDFium imports decrypted pages —
  which is what makes one page of a BSI licensed copy shareable (the dialog warns). Annotations are
  baked onto **only the extracted pages** (`BakeAnnotations(pages)` / `UnbakeAnnotations(pages)`
  overloads — not all 400) when `IncludeAnnotations`. Then `PdfMetadataWriter.AppendInfo` (source Info
  + new `Title`) and, if `IncludeBookmarks`, `PdfOutlineWriter.Append` of `SliceOutline(GetOutline(),
  old→new page map)`: bookmarks outside the range are dropped but their in-range children promoted.
- **`PdfPageRange.TryParse("3, 7-9, 12", pageCount, …)`** → sorted distinct 0-based indices (1-based
  input, `–`/`—` as dashes, reversed ranges OK, `,`/`;`), with a plain-language error; `Format` is the
  inverse ("1,3-5,8"). Page numbers are **file positions**, not page labels.
- App (`DocumentTabViewModel.Extract.cs`): **Pages ▾ "Extract pages…"** and thumbnails' right-click
  "Extract page(s)…" (`ThumbnailCommand.Extract`) pre-fill the thumbnail selection or the current page;
  a bookmark's right-click **"Extract chapter…"** (`BookmarksViewModel.ExtractRequested`) pre-fills
  `BookmarksViewModel.ChapterRange` — from its page to the page before the next bookmark in reading
  order that is *not* its descendant and starts on a *later* page (same-page bookmarks aren't
  boundaries), else to the end — and suggests "{doc} — {bookmark}" as the title.
  `ExtractPagesWindow` / `ExtractPagesViewModel` (range + live "n pages" + error, title, include
  annotations / bookmarks, open after) → `SaveFileDialog` (refuses the source path) → `Task.Run` →
  `OpenFileRequested` → `MainViewModel.OpenPathAsync`.

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

## Find in files (`PdfFolderSearch`)

- **`PdfFolderSearch.Search(filePaths, term, options, progress, ct)`** (`DocDr.Pdf`) full-text
  searches many PDFs and reports one `FolderSearchProgress` per file (hits-or-error, plus running
  totals) via `IProgress<T>` — no persistent index; every run is a fresh scan (there's no SQLite
  catalog yet to cache extracted text in). Per file it's just `PdfDocument.Load` +
  `new PdfSearch(doc).FindAll(term, options, ct, includeSnippets: true)`; `SearchHit.Snippet`
  (new, opt-in — the in-pane search leaves it off) is captured in the same text-page load as the
  rects, so there's no second text-extraction cost per hit. (Each folder file is a fresh `Load`, so
  it always takes the cold path below — one text-page load per page, same as before.)
- **Parallelism is deliberately capped low** (`min(4, ProcessorCount)`, via `Parallel.ForEach`)
  — every PDFium call is still serialised through `PdfiumLibrary.SyncRoot`, so extra workers
  can't parallelise the PDFium work itself. What they overlap is one file's disk read /
  `FPDF_LoadMemDocument64` parse with the *previous* file's search, which is otherwise wall-clock
  waste; more workers than that just queue on the same lock.
- App: `MainViewModel.FindInFilesCommand` opens a modeless `FolderSearchWindow` /
  `FolderSearchViewModel` (toolbar "Find in files…", home screen, Ctrl+Shift+F) — modeless, not
  `ShowDialog`, so results can be clicked while the main window is used. Folder enumeration
  (`Directory.EnumerateFiles(..., "*.pdf", ...)`) and the recurse-subfolders/match-case options
  live in the App, not `DocDr.Pdf` — the library just searches a list of paths it's given.
  Results stream into a `TreeView` (Document > page/snippet). Last folder / match-case /
  subfolders choice persists in `AppSettings`.
- **Progress reaches the UI through a `ConcurrentQueue`, not a live `Progress<T>` per file** —
  `Progress<T>.Report` posts to the dispatcher on *every* call, and a folder with thousands of
  PDFs turned that into thousands of dispatcher messages, enough to make the window sluggish to
  even drag around even though the scan itself runs on a background thread. `FolderSearchViewModel`
  hands `PdfFolderSearch.Search` a trivial `IProgress<T>` that just enqueues (no marshalling), and
  a `DispatcherTimer` (150 ms) drains the queue in capped batches — so UI work happens a fixed
  number of times a second regardless of scan speed, then does one uncapped final drain once the
  background task completes.
- **Activating a hit does *not* re-search the document** — `FolderSearchHitViewModel.SiblingHits`
  carries every hit the folder scan already found in that file (exact page + char range), passed
  through `FolderSearchActivation` → `MainViewModel.OpenSearchResultAsync` →
  `PdfPaneViewModel.GoToSearchHitAsync(knownHits, targetPageIndex, targetCharStart, targetCharCount,
  term, matchCase)`, which resolves rects via `PdfSearch.HitAt` (one page touched per known hit)
  instead of `FindAll` (every page in the document). Re-scanning the whole file again was the
  actual cause of the "long delay before opening" — a full-document `FindAll` is exactly as slow
  the second time as the first, so doing it on every click roughly doubled the wait on a big book.
  A hit whose range no longer checks out (the file changed since the scan) is silently dropped
  rather than failing the jump. Always targets `LeftPane`, like every other nav panel (bookmarks,
  clauses, thumbnails).
- **Search highlights are one box per line, not one per PDFium "rect"** — `PdfSearch.CollectRects`
  used to call `FPDFTextCountRects`/`FPDFTextGetRect`, which on a PDF that positions each
  character explicitly (a kerned or justified line's `TJ` array) returns one rect per glyph run,
  i.e. a strip of tiny per-character boxes instead of one box around the word. It now walks the
  match's own `FPDFTextGetCharBox` boxes and merges them with the new
  `PdfTextExtractor.MergeIntoLineRects` — the exact heuristic `PdfPaneViewModel.BuildLineQuads`
  already used for the click-drag text-selection highlight (vertical-overlap-continues-the-line,
  degenerate/space boxes skipped without breaking the run), now shared by both instead of
  duplicated. `BuildLineQuads` is a two-line wrapper around it. Each merged line box also gets a
  small pad (`LinePaddingFraction`, 12% of that line's height, applied after the merge so it can't
  affect the same-line decision) — a highlight sitting exactly flush against the glyph edges reads
  as slightly clipped, particularly on ascenders/descenders.

## Text cache + managed search (`PdfPageTextCache` / `PdfTextMatcher` / `PdfSearch.Search`)

- **Each page's text is extracted once per document** — `PdfDocument.TextCache` (internal
  `PdfPageTextCache`: page → `FPDFText_GetText` string, so string index == PDFium char index).
  **Keyed on `ContentVersion`**: a lookup/store at a newer version drops every entry; a store tagged
  with an older version is ignored (text read before an edit can't be served after it). Strings
  only (a 400-page standard ≈ a few MB; 64 M-char backstop); char boxes are *not* cached.
  `PdfTextExtractor.GetPageText` goes through it, so the clause scan, RAG chunker, OCR
  `PagesWithoutText` and headings all share one extraction with search.
- **Search no longer runs `FPDFText_FindStart` per page per query.** `PdfSearch.SearchPage`:
  cached text → `PdfTextMatcher.Find` in managed code, and PDFium is opened **only for a page with
  a match** (for its highlight boxes); uncached → one text-page load reads + caches the text and
  matches + boxes in that same load (so a cold search costs what the old loop did).
  `PdfSearch.PagesOpened` (internal) lets tests assert a warm search opens only matching pages.
  A page whose `FPDFTextCountChars` ≠ text length (non-BMP glyphs → surrogate pairs) falls back to
  PDFium's own find (`NativeFind`).
- **Matcher semantics** (`PdfTextMatcher`): term trimmed; any whitespace run in page text (incl.
  PDFium's `\r\n` between lines, and NULs) matches any whitespace run in the term — so a phrase
  split across a line break *is* found (PDFium's find didn't); `MatchCase` → ordinal, else
  `OrdinalIgnoreCase`; `MatchWholeWord` → letter/digit boundaries; `ConsecutiveMatches` →
  overlapping. A match maps back through the collapse map to a PDFium `(start, count)`.
- **Progressive**: `PdfSearch.Search(term, options, startPage, onBatch, ct)` visits `startPage`…end
  then wraps, hands new hits to `onBatch` on the worker thread (first hit at once, then ≤ every
  `BatchInterval` 120 ms, then a final flush) and returns all hits sorted. `FindAll` = `Search`
  from page 0 with no callback.
- **Pane** (`PdfPaneViewModel.SearchAsync`): starts at the current page (spread-left in TwoPage),
  posts batches to the UI `SynchronizationContext` → `MergeSearchHits` → `ReplaceSearchHits`, which
  keeps the selected hit by identity and **only the first batch selects + scrolls** (first hit at/
  after the start page) — later batches update the count/highlights and never move the view. The
  final `await` result replaces the list (same contents; batches were posted ahead of it).
  **Enter on an unchanged query (`_searchedQuery`) = next match**, not a re-search; Prev/Next don't
  restart a search that's still streaming. `ClearSearch` resets `IsSearching` itself (a cancelled
  run's `finally` no longer owns `_searchCts`).

## Main toolbar (`MainWindow.xaml`)

- **One `ToolBar` row of 16×16 stroke icons with tooltips**, grouped by how often each thing is used:
  File ▾ · Save · Undo · Redo │ Note · Draw · colour swatches · Text box · Callout · Cloud ·
  Shape (split) ▾ · Markup eye │ Pages ▾ · Tools ▾ │ Split · Side by side · Link scrolling · Read │ Theme ▾.
  Once-per-document actions live in the dropdowns: **File** (Open/New/Merge/Save/Save As/Print/
  Properties/Find in files), **Pages** (rotate/delete/insert PDF/blank page), **Tools** (Extract table
  as a checkable item, OCR, Remove watermark, Trim margins, Export chunks, Export HTML).
- A dropdown is a plain `Button` with its own `ContextMenu`, opened under it by `DropDown_Click`
  (code-behind) — the menu inherits the window `DataContext` and the Fluent `ContextMenu` style
  (a `Menu` inside a `ToolBar` would pick up the classic ToolBar style). Per-tab buttons carry
  `IsEnabled="{Binding HasTabs}"` since a null `SelectedTab.XCommand` binding leaves a control enabled.
- Icon geometries are `Tb*` resources in `MainWindow.xaml`, drawn with `NavTabIcon` (stroke = the Path's own
  inherited `TextElement.Foreground`, so checked/disabled states work); `MenuIcon` is an alias for menus. **Don't use a
  `FindAncestor` (ButtonBase / MenuItem) binding for the stroke** — items in an unopened menu or a
  collapsed ToolBar overflow aren't in a visual tree yet, and the lookup logs binding error 4.
- **Shape split button**: `DocumentTabViewModel.IsBasicShapeActive` (on while rectangle/ellipse/line/arrow
  is armed; turning it on arms `LastBasicShape`, which `OnShapeToolChanged` records). Its icon swaps
  by `LastBasicShape`; the ▾ menu picks the shape. Markup toggle swaps eye / eye-off and its tooltip is
  `MarkupToolTip`. Theme is a ▾ menu (`Theme_Click`), not a ComboBox.

## Pane status strip + find bar (`PdfPaneView.xaml`)

- **Page nav / zoom / view mode moved to a thin strip at the *bottom* of each pane** (Grid row 1;
  the page `ListBox` and its popups are row 0). There is no pane toolbar above the page any more.
  The strip hides in read mode like the old toolbar did.
- **Search is a floating find bar** (`FindBar`, top-right of the page area, drawn after `PageList`
  so it sits on top), shown by `PdfPaneViewModel.IsSearchBarOpen`. Opened by Ctrl+F (the pane's
  own `PreviewKeyDown`, so the pane you're in gets it; a window `KeyBinding` to
  `SelectedTab.LeftPane.OpenSearchBarCommand` is the fallback when focus is outside every pane) or
  the strip's magnifier toggle (`SearchToggle_Click`). `OpenSearchBar()` raises
  `SearchBarFocusRequested` → the view focuses `SearchBox` at `Input` priority. Esc / ✕ →
  `CloseSearchBarCommand` = hide + `ClearSearch` (drops the highlights). `GoToSearchHitAsync`
  (find-in-files result) opens the bar so the term and match count are visible.
- **The active hit is centred, not its page top-aligned** (Continuous / SinglePage-scrolling only;
  paged + grid keep the page jump): `MoveToHit` → `HitCentreFraction` (hit rects' centre as fractions
  of the slot's layout box, same transform as the overlay) → `ScrollToHitRequested(page, y, x)` →
  `PdfPaneView.OnScrollToHitRequested` → `AlignPage(..., viewportAnchor: 0.5)` (measures `fraction`
  against the slot's `LayoutHeight`, not the container) + horizontal centring when the page is
  wider than the pane.
- **Search highlights get extra screen-space padding** in `ApplyHighlights`
  (`SearchHighlightPadY/X` = 22 % / 30 % of the line height, min 1.5 / 2 DIP, swapped on 90/270
  pages) on top of `MergeIntoLineRects`' shared 12 % — the selection highlight stays tighter.
  Corners rounded 2 DIP.

## Navigation panel (icon rail)

- **`NavigationRailView`** is a ~40px always-visible strip (column 0 of `DocumentTabView`) with
  five icon buttons — Pages / Bookmarks / Clauses / Comments / Annotations (markup). Each runs
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
- **Tick** (`PdfAnnotationKind.Tick`, appended last to the enum; `ShapeTool.Tick`; toolbar ✓ after
  Cloud) — stamp-backed, box in `Quads[0]`, drawn from `PdfAnnotation.TickPoints(box)` (3 points,
  shared by `PdfStampAppearance.DrawTick` and the overlay `Polyline`, mapped point-by-point so it
  turns with a rotated page). `NewTick` sets `LineWidth = DefaultTickLineWidth` (3 pt). **The tool
  is not one-shot**: a click drops an 18 pt tick (`DefaultTickSize`) centred on the point, a drag
  sizes it, the new tick isn't selected (no format bar per tick), and Esc / the toolbar disarms.
  Moves/resizes/selects like the other box kinds; format bar = colour + width. It's markup, so it
  lists in the Annotations panel.
- **Shape colour round-trips via `/DocDrShape`, not `FPDFAnnotGetColor`.** `FPDFAnnotGetColor`
  fails on a reloaded stamp that carries an appearance stream, and `ReadColor` then falls back to
  yellow (`0xFFFFD54F`). `PdfShapeCodec.Shape` carries a `uint? Color` (null when 0); `Decode`
  uses `shape.Color ?? colorArgb`.

## Measuring (`PdfMeasure` / `PdfMeasureScale`, modelled on pdfcraft's measure tool)

- **Kinds** `MeasureDistance` / `MeasurePerimeter` (open polyline) / `MeasureArea` (closed polygon,
  shoelace) — appended to `PdfAnnotationKind`, **stamp-backed**: vertices in `Strokes[0]` (exposed as
  `PdfAnnotation.Vertices`; `Quads` empty), `PdfAnnotation.Scale` = the `PdfMeasureScale` it was taken
  at. **Every measurement stores its own scale** (like ISO 32000 §12.9's per-annotation `/Measure`), so
  changing a page's scale never changes earlier values. The value is always *computed* from vertices
  + scale (`PdfMeasure.Value` / `Caption`), so dragging a vertex updates it.
- **`PdfMeasureScale(UnitsPerPoint, Unit, Label, Decimals)`**: `FromDrawingRatio(50, "m")` ("1:50"),
  `FromRatio(paper, unit, real, unit)`, `Calibrate(pagePoints, realLength, unit)`, `Default` = 1:1 sheet
  mm. Units mm/cm/m/in/ft. Area captions are `unit²`.
- **Saved** via `/DocDrShape` (vertices in the `Leader` array + `Upp`/`Unit`/`ScaleLabel`/`Dec`). PDFium
  can't write `/Measure` dictionaries, so other viewers see only the appearance: lines (area tinted
  `0x33` alpha), a distance's end bars (`PdfMeasure.EndTicks`), and the caption on a white backing
  (`CaptionRect`, Helvetica metrics). The annot `/Rect` = `AppearanceBounds` (vertices + caption) or the
  caption would be clipped; `/Contents` = `PdfMeasure.Describe` ("Distance 3.25 m") and is **not**
  read back as a note (decode nulls it). Acrobat-authored `/Measure` annotations are not imported.
- **Interaction** (`PdfPaneViewModel` "Measuring" region; `ShapeTool.MeasureDistance/Perimeter/Area/
  Calibrate`): **click-by-click, not drag** — `PdfPaneView` routes a mouse-down to `MeasureClick`
  (or `MeasureFinish` on a double-click for perimeter/area) *before* the generic shape branch, rubber-
  bands on mouse-move (`MeasureHover`), and in `PageList_PreviewKeyDown` Enter = finish, Backspace =
  drop last vertex, Esc = abandon (a second Esc disarms). Shift = 45° (`Constrain45`). Distance /
  calibrate finish on the 2nd click. A click on the same spot as the last vertex adds nothing (the
  first half of a double-click). **Tools stay armed**; a measurement lives on one page (a click on
  another page restarts there). Live preview = `PageSlotViewModel.MeasurePreview*` (dashed accent
  line, tinted area, value badge by the pointer; an area also shows its running perimeter). New
  measurements use the swatch colour, except the default yellow → `DefaultMeasureColorArgb` (red).
- **Editing**: select by a segment / inside the area / the caption (`HitsMeasurement`); move like any
  shape (`MoveShape` shifts all vertices); **every vertex is draggable** — `TryHitLineEndpoint` /
  `MoveLineEndpoint` were generalised from a line's two ends to any measurement vertex. Format bar =
  colour + line width. Listed in the Annotations panel with value + scale.
- **Scales** (`DocumentTabViewModel.Measure.cs`): a document scale + per-page overrides, *session
  state* — on open `InferScales` recovers them from the newest measurement overall / per page; a
  structural page edit drops the per-page overrides (indices shift). Panes ask via `ScaleProvider`.
  `MeasureScaleWindow` / `MeasureScaleViewModel`: ratio form (1 : N + unit), or — after
  `ShapeTool.Calibrate` + two clicks (`CalibrationMeasured`, dialog posted via `BeginInvoke`) — the
  known length; decimals; apply to all pages / this page. The bottom strip shows `ScaleText`
  ("Scale 1:50 · m"), click → Set scale. Toolbar **Measure ▾** (lit while a tool is armed):
  Distance / Perimeter / Area, Set scale…, Calibrate…, Export measurements (CSV) — `MeasurementsCsv`
  (invariant numbers, formula-leading cells prefixed `'`, RFC-4180 quoting).
- **Snapping** (`PdfSnapGeometry`, `DocDr.Pdf`): `Extract(doc, page)` walks every path object, recursing
  into Form XObjects (`FPDFFormObjCountObjects/GetObject`, depth ≤ 16) and composing each object's
  `FPDFPageObjGetMatrix` with its parent's; reads MOVETO/LINETO/BEZIERTO (**PDFium reports a cubic as
  three BEZIERTO points** — c1, c2, end — flattened to 8 segments) and `FPDFPathSegmentGetClose`.
  Capped at `MaxSegments` (20 000 → `Truncated`). Glyphs, images, clipping and OCG visibility are
  ignored. Indexed in a 24-pt grid (each segment registered in the cells it passes through, sampled
  at half-cell steps). `Snap(p, tol)` priority: **end point > intersection (among the 48 closest
  segments) > midpoint > nearest point on a line**, the stronger kind winning even if a weaker one
  is nearer. App: `PdfPaneViewModel` loads a page's geometry on first measuring hover
  (`LoadSnapGeometryAsync`, `Task.Run`, dropped if `_pageDataGeneration` moved; cleared in
  `ReloadPages`), snaps hover + clicks within `SnapTolerancePx` (9 screen px), and draws
  `PageSlotViewModel.SnapMarker`/`SnapKind` (square / ✕ / triangle / circle + caption). Hover runs
  whenever a measuring tool is armed (so the marker shows before the first click); **Ctrl
  suspends, Shift's 45° constraint overrides**, and `DocumentTabViewModel.SnapToDrawing` (Measure ▾
  "Snap to drawing lines", default on) mirrors to `PdfPaneViewModel.SnapEnabled`. Calibration clicks
  snap too. **Dragging a measurement vertex snaps as well**: `BeginLineEndpointMove` records
  `_vertexSnapOrigin` (page + start vertex, measurements only — line / arrow ends stay free), and
  `Preview/EndLineEndpointMove(dx, dy, suspendSnap)` run `SnapVertexDrag` (snap origin + drag, return
  the adjusted drag); "did it move" is judged on the raw drag so a sub-point snap still commits.
  The marker clears on drop / cancel.
- **Not yet** (pdfcraft has them): inserting vertices, compound units (ft-in), different X/Y scales.

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
- `TesseractOcrEngine.Recognise` encodes the BGRx `RenderedPage` as an in-memory 24-bit BMP
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
  existing subtype-1/9 annotations via `PdfAnnotations.ReadFromPage` then `StripManaged`s them off
  the original handle (== `_sources[0]`), so `Rebuild()` imports a clean base and the WPF overlay
  is the only thing drawing them. **This loop is the dominant cost of opening a file** — it's the
  only place that touches every page up front — so it does exactly one `FPDF_LoadPage` per page
  and reuses that same page handle for rotation, the annotation read, and the strip, rather than
  the three separate load/close round trips each used to do on its own (`GetRotation` /
  `PdfAnnotations.ReadLocked` / a since-removed `StripManagedAnnotations`, each loading the page
  itself). Halved open time on a 600-page synthetic benchmark (~490ms → ~260ms). `ReadLocked`
  (loads the page itself) still exists for callers that only want the annotations, e.g.
  `PdfAnnotations.Read`. `SaveToBytes` bakes them onto the pages via
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
  record, so they snapshot with history and are undoable like any other annotation edit.
  **Replies are posted from the Comments panel, not the editor** — `AnnotationEditorViewModel`
  is text (+ highlight colour) only and passes the existing `Replies` through untouched.
- **Comments panel** (`NavigationTab.Comments`, `CommentsViewModel` / `CommentsPanelView`) is split
  from the **Annotations panel** (`NavigationTab.Annotations`, `AnnotationListViewModel`, now markup
  only). `CommentClassifier.IsComment` = a note, or a highlight with note text or replies; every
  annotation is in exactly one panel. Comments are grouped by page (collapsible `CommentPageGroup
  ViewModel`); each `CommentCardViewModel` shows icon · author · date · status chip · ✓ · ⋯, the
  text, the reply thread, and (selected card only) an "Add a reply" box (Ctrl+Enter / Post). The
  ⋯ menu: Edit… (opens the editor via `LeftPane.EditAnnotationCommand`), Set status ▸, Mark with
  checkmark, Copy text, Delete. Every change is a new record → `UpdateRequested` →
  `DocumentTabViewModel.OnCommentUpdateRequested` → `Document.UpdateAnnotation` (undoable) →
  `AnnotationsChanged` → `Comments.Reload`, which **keeps the selected card, half-written replies
  (by id) and collapsed page groups**. Selecting a note on the page (`LeftPane.SelectedAnnotationId`)
  selects its card (`Comments.Select`) and `BringIntoViewWhen` scrolls it into view.
- **Review status + checkmark** — `PdfAnnotation.Status` (`PdfReviewStatus`: None / Accepted /
  Cancelled / Completed / Rejected, the PDF "Review" state model) and `IsMarked` (the "Marked"
  model). Standard PDF puts these on a separate `/IRT` reply annotation, which PDFium can't link, so
  they round-trip as private keys `/DocDrStatus` (enum name) and `/DocDrMarked` ("true"), written
  only when set (`PdfAnnotationWriter`) and read for every managed subtype (`PdfAnnotations`
  wraps each `result.Add` in a local `Add` that applies them). Ignored by other readers.
- **Note icon** = a filled speech bubble (`NoteIconBody` + `NoteIconLines` in `App.xaml`, 24×24,
  fill = note colour, darkened outline/lines) — the page pin (accent frame when selected) and the
  Comments cards both use it. The rail's Comments button keeps the outline bubble
  (`IconAnnotations`); Annotations uses the pen (`IconMarkup`).
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
  **The text-selection mouse-down branch calls `PageList.Focus()`** (added alongside `Copy` —
  the shape-box and existing-annotation branches already did this, plain text-selection didn't).
  Without it, `Ctrl+C`/`Ctrl+V` never reach `PageList_PreviewKeyDown` after a drag-selection —
  focus just stays wherever it was, since a mouse-captured drag doesn't move keyboard focus the
  way a plain click does. Caught by testing the real interaction, not by reading the code — it
  compiled fine and looked correct.
- **Copy** (`PdfPaneViewModel.CopySelectionCommand`) puts the selected passage's plain text on the
  clipboard via `Citations.CopyToClipboard` — doesn't clear the selection, matching copy behaviour
  anywhere else. Reachable via the selection popup's "Copy" button or `Ctrl+C`
  (`PageList_PreviewKeyDown`, alongside the existing `Ctrl+V` paste branch — gated on
  `HasPendingSelection` so it doesn't swallow `Ctrl+C` when nothing's selected).
  **`EndTextSelection` builds two strings from the same char range, not one:** `_pendingText`
  (flattened to one line — `.ReplaceLineEndings(" ")` — what a citation quote wants, since the
  PDF's line-wrap points are just where that line ran out of column width) for `CiteSelection`,
  and `_pendingRawText` (`.ReplaceLineEndings()` with no argument — normalises whatever line-break
  form PDFium used to `Environment.NewLine`, but keeps the breaks) for `CopySelection`. PDFium's
  char-level extraction does emit real `\r`/`\n` characters between separately-positioned lines
  (confirmed directly against a two-line fixture) — `_pendingText` was silently throwing that
  structure away for every consumer, which was fine when the only consumer was the citation quote
  but wrong once a plain copy needed the same selection.
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

- One `BackgroundRenderQueue` worker rasterises pages. **Thumbnail-sized requests (≤ 320 px)
  go first**, oldest-first (the strip fills top-down); larger page renders keep newest-first
  (the page just scrolled to). `Clear(owner)` (that pane's zoom / DPI change) drops only *that
  owner's* page requests — thumbnail requests never go stale. **The queue is one app-wide instance
  shared by every pane in every tab**: an unscoped clear (as it once was) wiped other panes' pending
  renders too, so a newly opened tab sat on its blurry placeholder until a scroll re-requested the
  page. Closing a tab removes just that document's requests (`RemoveWhere(r.Document == closing)`). Without the thumbnail priority a fast page-scroll starved the strip.
- **The worker `catch`es every per-request exception and keeps looping** — an uncaught throw
  there kills the worker and *every* page then renders blank forever (this was a real bug:
  extreme zoom → a `new byte[stride*height]` of hundreds of MB → `OutOfMemoryException` → dead worker).
- **The page cache holds finished, frozen `BitmapSource`s** — `PageImageService`'s
  `SizedLruCache<PageImageKey, BitmapSource>` (the LRU itself is generic and WPF-free in
  `DocDr.Pdf`, so it's unit-tested there). These are the very objects the page slots display, so an
  on-screen page is held once and a hit is free (no copy, no allocation). It replaced
  `CachingPageRenderer`, a raw-BGRA LRU *below* `PageImageService` that held every visible page
  twice (cached buffer + its `BitmapSource` copy) and re-copied on every hit. Capped at **128 MB /
  48 entries** — page bitmaps are ~4-6 MB each at 150 % DPI, so ~20 stay hot. `Purge(doc)` on tab
  close / `Document.Changed`. A single render bigger than a third of the budget (a page at extreme
  zoom) **isn't cached** — it would evict a run of reusable normal pages; the slot still holds it.
- **`PageImageKey` includes `PdfDocument.ContentVersion`** (bumped by `RaiseChanged`, i.e. on every
  `Changed`). Without it, a render in flight when a page edit purges the cache could land *after*
  the purge and then be served as the edited page (stale rotation / wrong page after a delete).
- **A cache-miss render goes PDFium → `Marshal.AllocHGlobal` scratch (`PageRenderer.RenderInto`)
  → `BitmapSource.Create(IntPtr…)` → freed.** No per-render managed `byte[]` (each was a multi-MB
  LOH allocation for the GC to clean up). The scratch is per call, not pooled, so the short-lived
  print / watermark-preview `PageImageService`s (no cache) have nothing to leak.
- `MaxRenderEdge` is **4800**: the long-edge cap on a full-page bitmap. Reading zoom renders
  1:1 (`PageImageService` bakes `96 × deviceScale` DPI into the `BitmapSource` so its DIP size
  equals the on-screen box — no WPF resample); `PageRenderer` uses greyscale AA, not `LcdText`,
  since the bitmap gets resampled during a zoom gesture.
- **Past the cap the visible slice is re-rendered crisp (viewport render).** `PdfPaneView.
  RefreshDetailRegions` (every scroll / visible-range refresh) reports each on-screen page's
  visible rect in page-local DIP → `PdfPaneViewModel.SetPageDetailRegion` (debounced 110 ms,
  skipped unless `NeedsDetailRender` — long edge > `MaxRenderEdge`). `RequestDetailRender` pads
  the rect, enqueues a `RenderRequest` with a **`PageRenderRegion`** (full-page px size +
  up-left offset); `PageRenderer` draws the whole page at full size shifted so only that slice
  lands in the buffer (`FPDF_RenderPageBitmap` with negative `start_x/y`). Result → `PageSlot
  ViewModel.DetailImage` + `DetailLeft/Top/Width/Height`, laid over the soft base `Image` in a
  `Canvas`. `PageImageService` never caches region renders (scroll-transient, no point).
  **A scroll that stays inside the tile on screen doesn't re-render** — `DetailTileCovers` checks
  the visible rect + 32 DIP against `PageSlotViewModel.DetailShownKey` (the *delivered* tile — a
  merely requested one can be dropped by `_queue.Clear()`, so it isn't trusted); the tile is padded
  120 DIP, so small scrolls cost nothing. It used to re-render on every scroll stop. Cleared on zoom / device-scale / reload / page leaving the visible range
  (`ClearAllDetail` / `ClearPageDetailRegion`). Continuous + SinglePage only, not Grid/TwoPage.
- **Text I-beam on hover:** `PdfPaneView.UpdateHoverCursor` (from `PageList_MouseMove` when no
  button is down) hit-tests `PdfPaneViewModel.IsOverText` (the page's char boxes) and sets
  `PageList.Cursor = IBeam`, or `ClearValue` so the style's tool-cursor triggers win. The
  char-box cache (`_charBoxCache` + `_charBoxOrder`) is an 8-page LRU so hover doesn't retain
  every page the pointer crosses.
- `CappedRenderSize` is used for both the enqueue size and the `OnPageRendered` stale check, so
  they agree.
- **Page bitmaps are opaque `Bgrx` → WPF `Bgr32`, not `Bgra`/`Bgra32`.** A page is always painted
  on white, so alpha only cost: slower PDFium compositing, a WPF format conversion on upload, and
  alpha blending every frame. `RenderedPage.Pixels` is BGRx — every consumer (trim margins, OCR's
  `BmpWriter`, tests) reads only B/G/R. (`PdfStampAppearance`'s image-object bitmap is separate and
  stays `Bgra` — that one genuinely needs alpha.)
- **`PageSlotViewModel.IsPixelExact`** (set via `PdfPaneViewModel.SetLayoutSize` — every layout
  path goes through it — and `OnPageRendered`): the bitmap was rendered at the box's full
  device-pixel size and isn't capped. The page `Image` then switches to
  `BitmapScalingMode=NearestNeighbor` (a style `DataTrigger`), so a sub-pixel layout/scroll offset
  can't be interpolated into blur; `HighQuality` otherwise (mid zoom gesture, stale size, capped).
  The detail tile is always 1:1, so it's `NearestNeighbor` outright.
- **Loading placeholders + synchronous cache hits.** `PdfPaneViewModel.RequestRender` first asks
  `IRenderQueue.TryGetCached` (→ `PageImageService.TryGetCached`, a lock + dictionary lookup, fine
  on the UI thread): a hit is shown at once via `ShowPageImage`, never queued behind the worker's
  current render. On a miss, a page with no `Image` yet also gets `RequestPlaceholder` — a render at
  exactly `ThumbnailStripViewModel.ThumbnailPixelSize` (118 DIP wide × scale), which the queue serves
  first (it's ≤ 320 px, i.e. thumbnail priority) — shown stretched in `PageSlotViewModel.
  PlaceholderImage` (an `Image` under the page `Image`, `Linear` scaling) until the real image lands,
  then dropped. Same pixel size as the strip's thumbnails ⇒ **shared cache entries**: a thumbnail
  the strip drew is a free placeholder and vice versa (the strip also checks `TryGetCached` first).
  Skipped when the full render is < 2× the placeholder (small grid tiles). Placeholder requests are
  owned by the pane, so `RemoveWhere` drops them for pages that scroll away like any other.
- **Queued renders for pages outside the realised window are dropped** —
  `UpdateVisibleRange` calls `IRenderQueue.RemoveWhere(owner == this && page outside [first,last])`.
  Without it a fast fling left the worker rasterising every page it passed (holding the global lock)
  before reaching the one on screen. Thumbnails have a different `Owner`, so they're untouched.
- **No PDFium reads on the UI thread for overlays.** `BuildLinkOverlays` draws only what's cached
  and hands missing pages to `LoadLinkDataAsync` (`PdfLinks.Read` + `PdfCrossReferences.Scan` in a
  `Task.Run`, resumes on the UI thread to fill `_linkCache`/`_crossRefCache`, then rebuilds). The
  hover I-beam's `IsOverText` likewise returns false on a char-box cache miss and kicks off
  `PrefetchCharBoxesAsync`. Both used to read inline, and so waited on `SyncRoot` behind whatever
  page the render worker was rasterising — a scroll/hover stall. `_pageDataGeneration` (bumped by
  `InvalidatePageDataLoads` in `ReloadPages` / `SetClausePageMap`) makes a stale result drop itself.
  Selection / copy / annotation paths still call `CharBoxes` synchronously (by then hover has
  usually warmed the cache).
- **Visible-range detection reads WPF's realised containers only — there is no parallel height
  model.** `PdfPaneView.VisiblePageRange()` walks `PageList.Items` → `ContainerFromItem` → the
  containers that actually intersect the viewport (`TransformToVisual(scrollViewer)` + `ActualHeight`),
  and it's the single source of truth for `RefreshVisibleRange` / `TopVisiblePageIndex` in **every**
  scrolling mode — Continuous, SinglePage and Grid (in Grid the containers are `PageRowViewModel`s;
  their slots' page indices are unioned in). When nothing is realised yet (one frame at startup /
  just after a jump) the fallback is `UpdateVisibleRange(CurrentPage-1, CurrentPage-1)` — realise
  the current page, the next layout pass reads it. The old `LayoutHeight`-sum offset methods
  (`GetPageAtOffset` / `GetRowAtOffset` / `GetPageOffset` / …) are gone — they drifted from the
  virtualising panel's pixel-extent estimate for a pass or two after a zoom and rendered the wrong
  pages. `PageSlotViewModel.LayoutHeight` / `PageRowViewModel.RowHeight` remain — they're the
  per-item template dimensions WPF measures against, not an offset model. Page jumps
  (`OnScrollToPageRequested`) use `ScrollIntoView` + real container Y, never an offset sum.
- `UpdateVisibleRange` clamps the realised span to `MaxRealizedPages` (16) as a backstop against
  a bad reading queuing hundreds of large renders, **and frees `PageSlotViewModel.Image` for
  slots more than `ImageKeepMargin` (6) pages outside that span** — the bitmap is otherwise held
  for the session and a long standard retains every page it has shown (~4 MB each → GBs).

## Side-by-side documents (`TabGroupViewModel` / `ScrollLink`)

- **Two docked tab strips.** `MainViewModel.LeftGroup` / `RightGroup` (`TabGroupViewModel`: `Tabs`,
  `SelectedTab`, `IsActive`) split the open tabs by side; `MainViewModel.Tabs` is still the flat list of
  every tab (save-on-exit, `UniqueTitle`, "already open?"). The right group only has tabs while docked
  (`IsSideBySide`). Rules (`MoveTab` / `CanMoveTab` / `NormaliseGroups`): the left strip is never empty
  while tabs are open — empty it and the right side's tabs slide over; the only open tab can't be split
  off on its own; moving the last right-hand tab back ends side-by-side. New tabs open on the active side.
- **`MainViewModel.SelectedTab` is no longer an auto-property** — it's the *active group's* selected tab
  (setter activates the tab's group). That one change makes every toolbar / shortcut / title binding
  (all `SelectedTab.*`) follow the side the user last clicked or focused into (`MainWindow`
  `GroupHost_PreviewMouseDown` / `PreviewGotKeyboardFocus` → `ActivateGroup`). `RaiseSelectedTabChanged`
  re-does what the old property's change hooks did (title follow, leave read mode).
- **View:** both sides are the same `DocumentTabControl` style (`MainWindow.xaml` resources) in
  `LeftGroupHost` / `RightGroupHost`, `DataContext` = the group; active side gets an accent top rule
  while docked. Column widths are set in code-behind (`ApplyGroupLayout`, from `ShowLeftGroup` /
  `ShowRightGroup` — read mode shows only the active side) and remember the splitter's split.
- **Drag a tab to dock it.** `TabItem` `EventSetter`s start a `DragDrop.DoDragDrop` (format
  `"DocDr.DocumentTab"`) past the system drag threshold (never from the ✕); `TabDropOverlay` (a
  transparent `Canvas` over `TabArea`, visible only during the drag) shows one zone — the right half
  when nothing is docked, else the other side's area — and `Drop` calls `MoveTab` via
  `Dispatcher.BeginInvoke` (moving inside the drag loop would detach the drag source). **The window's
  file-drop `Preview*` handlers must ignore that format** — they mark every drag handled otherwise.
  Also: tab context menu / toolbar "⇆ Side by side" → `DocumentTabViewModel.MoveToOtherSideCommand`.
- **Linked scrolling** (`MainViewModel.IsScrollLinked`, toolbar "🔗 Link scrolling"): `RelinkScrolling`
  builds a `ScrollLink` between the two visible tabs' `LeftPane`s whenever either side's visible tab
  changes (held off during multi-step moves by `Rearranging`, so intermediate pairings never copy
  zoom). Alignment is **by page + fraction, not pixels** — `ViewportPosition(PageIndex, Fraction,
  HorizontalFraction)`, zoom- and page-size-independent — with the page offset the two had when
  linked (line them up, then link). Zoom (incl. fit modes) follows too (`PdfPaneViewModel.ApplyZoom`,
  render debounced like a wheel zoom). Paged / grid modes follow `CurrentPage` instead.
- **Echo control** (the part that's easy to break): `PdfPaneView` reports its position after every
  non-programmatic `ScrollChanged` but flags it user-initiated only if `PageList.IsMouseOver ||
  IsKeyboardFocusWithin`; a jump the user asked for (`OnScrollToPageRequested` → `AlignPage`) reports
  as user-initiated when it settles; a sync (`OnScrollToPositionRequested` → `AlignPage(fromSync:
  true)`) never does. `PdfPaneViewModel.ViewportScrolled` only fires for user-initiated reports, and
  `ScrollLink` also drops a report that `IsNear` what it just pushed to that pane.

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
- **The scan is on demand, not automatic on open or after every edit.** It used to run
  unconditionally in `DocumentTabViewModel`'s ctor *and* again on every `Document.Changed`
  (any rotate/delete/insert/undo) — a full page-by-page `GetPageText` pass competing for the same
  global PDFium lock as whatever page was actually being rendered, worthwhile for a 400-page
  Eurocode but wasted (and a real drag on responsiveness) on the other ~95% of documents opened.
  Now: the ctor only calls `ClausesViewModel.LoadFromCache` when `PdfDocument.CachedClauseStructure`
  is already set (instant, no scan); `OnDocumentChanged` doesn't rescan at all after a structural
  edit (matches the outline's existing precedent — page numbers go stale, regenerate manually);
  and the actual scan (`ClausesViewModel.Load`, unchanged internally) only runs from
  `DocumentTabViewModel.ScanClausesCommand`, wired to a "Scan for clause headings" button in
  `ClausesPanelView` (`NotYetScanned` / `IsEmptyResult` states — same `RelativeSource
  AncestorType=NavigationPanelView` command-reach pattern as the Bookmarks panel's "Generate from
  headings" button).
- **`PdfClauseCache`** (`DocDr.Pdf`, internal) caches a scan's `PdfCodeStructure` inside the PDF
  itself — a private Flate-compressed stream object referenced from the Catalog (`/DocDrClauses`),
  appended as an incremental update exactly like `PdfOutlineWriter`/`PdfMetadataWriter` (PDFium has
  no setter for a new Catalog entry either, and the three compose fine in one save — each preserves
  Catalog keys it doesn't understand). `PdfDocument.SetClauseStructure` (called from
  `ClausesViewModel.Load`'s continuation, only for a non-empty result) sets `_clauseStructure` and
  marks the document dirty, same as `SetOutline`; `SaveToBytes` appends it after the outline.
  `Load` restores `_clauseStructure` straight from the field (not through the dirty-marking
  setter — it's session state coming back, not a new edit) via `PdfClauseCache.TryRead`, which
  does one linear byte-scan for the `/DocDrClauses` marker before paying for any string/regex
  parsing — the overwhelming majority of opens are files with no cache at all, and that has to
  stay cheap. An empty result is never persisted (nothing to skip a rescan for), and a stale
  cache (structural edit since the scan) isn't auto-invalidated, same rationale as outline.
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
  are dropped). **Ruling lines** are detected from both stroked lines and the thin *filled*
  rectangles real code PDFs use for grids: `FPDF_SEGMENT_LINETO` is **0** (not 1 — the constants
  are LINETO 0 / BEZIERTO 1 / MOVETO 2), and an edge longer than the selection is a page
  border, not a rule. Column bands are sorted L→R (`Bands` returns them descending). Robustness
  against a loose marquee: rotated glyphs (`FPDFText_GetCharAngle` via `GetCharBoxesWithAngle`,
  the "uncontrolled copy" margin strip) are dropped; whitespace column search is clamped to the
  x-span the data rows actually cover; a char more than ~1.5 row-heights / ~3 char-heights
  outside every band is dropped rather than snapped (`IndexOfBand` slop). `TrimEmptyEdges` cleans
  margin rows/cols. App: `DocumentTabViewModel.TableSelectActive`
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
- **Text comes from `PdfChunkText.ReadPage`, not `GetPageText`** — per char `FPDFTextGetUnicode` +
  `FPDFTextGetCharBox`, then the pure `BuildPage` (unit-tested with real char sequences). **PDFium
  returns a non-BMP character (𝑘 U+1D458) as two chars, one per UTF-16 surrogate, sharing the glyph's
  box** — `BuildPage` re-pairs them (a lone half → U+FFFD). Per page it yields
  `ChunkLine(Text, InMargin, HasMath)`: **doubled glyphs** — a *non-ASCII* code point at IoU ≥ 0.6 with
  one of the last 12 kept on the line — are dropped. The cause is usually the font's own ToUnicode:
  Word's maths export maps one glyph to a doubled string (`<0747> <D835DC58D835DC58>` = "𝑘𝑘"). ASCII
  is exempt because a ligature glyph legitimately expands to repeated letters at one box ("ff"); maths letters / ℎ are NFKC-normalised to plain ones (𝜏→τ, 𝑐→c); `InMargin` = every glyph in
  the top/bottom 8 % band. **`PageNumberLine` only drops a line when `InMargin`** — mid-page a lone
  "1"/"3"/"d"/"v" is an equation fragment (a stacked ⅓ exponent, a variable under a fraction bar), and
  the old filter (digits + roman numerals `[ivxlcdm]`) silently deleted them.
- **Quality flags** (`PdfTextQuality`): every `RagChunk` has `quality` (`ok` / `check` / `suspect`) +
  `issues` (plain-language reasons). `suspect` = U+FFFD / private-use / control chars ("unmapped") or
  characters outside the expected technical-Latin/Greek/Cyrillic/maths/symbol ranges ("unexpected" —
  a symbol font with a broken `/ToUnicode` comes out as Syriac/Oriya letters: `݇` for k, `ଵ ଷ` for ⅓).
  A 128-code-point block holding ≥ 20 % of the document's otherwise-unexpected letters is the
  document's own script (`ScriptProfile`), not flagged. `check` = the chunk has maths
  (`IsMathCodePoint`: maths alphanumerics, operators, super/subscripts) whose 2-D layout is flattened.
  Only flagged, not repaired.
- **Most of that garbage is DocDr's PDFium (4688), not the PDF.** Checked against BS EN 1992-1-1:2023
  p.143 / Annex O: the Word-made `CambriaMath` Type0 font's ToUnicode *does* map 𝑘, 𝑓, 𝑑 and the
  script-size `1`/`3` of a ⅓ exponent, but PDFium ~4700 ignores some of those entries (others written
  identically work) and falls back to the raw CID as Unicode — CID 0747 → U+0747 Syriac, 0B35 → Oriya
  `ଵ`, 1246 → Ethiopic `ቆ`. PDFium 4763 reproduces the agent's exact output; current PDFium
  (pypdfium2 / chromium 8076) extracts the same pages correctly except glyphs the map itself sends to
  U+FFFD (big parentheses, the radical). Annex O: 166 garbage chars on 4763 vs 16 on current, and
  4763 also loses "(O.5)". **Fixed by moving to a current native PDFium** (see "PDFium native
  build"); the quality flags still catch what the font itself leaves unmapped. `RagChunkResult.SuspectPages` / `DoubledGlyphsRemoved` feed the export summary dialog.
- **Review fixes (independent check against page images + a textbook run):**
  - **Sections are per paragraph, not per page.** `SectionMarkers`: the PDF's **bookmarks win when ≥ 3**
    (a textbook's clause scan mistook "1.35 Gk" for a heading and swallowed the book), else the clause
    tree minus titles with no 3-letter word. A heading switches `current` at the paragraph that prints
    it (`SectionMarker.StartsParagraph`: clause number, or the bookmark title's first 40 chars); one not
    found on its page applies from the page top. `section_title` = the first unit's; `sections` = all
    spanned. A section start opens a new chunk (once ≥ `MinChars`) **with no overlap**.
  - **Overlap is `context_before`, not inline** — `text` never repeats the previous chunk.
  - **Boilerplate** (`Boilerplate.Detect`): verbatim on ≥ 50 % of pages, or a header/footer-band line
    on ≥ 30 % with digits blanked (`MarginKey`); `LicenceStamp` (Licensed copy / Provided by … under
    licence / Not for Resale…) is always cut from the marker to line end — it leaked on near-blank pages.
  - **Title**: metadata unless `PlaceholderTitle` ("untitled", "30187350.pdf", "Microsoft Word - …") →
    the most frequent top-band running line with ≥ 2 words (`RunningTitle`, end page numbers stripped)
    → file stem. `CleanTitle` normalises U+2010–2012 and trailing dots. `source_path` = file name only.
    `pdf_page_start/end` (1-based) + `page_label_start/end` (`GetPageLabel` or the 1-based number).
  - **Glyph repairs** (`BuildPage`): same-spot duplicates dropped for ASCII too (fake bold), except a
    second `f` at one box (ff/ffi ligature); zero-height boxes compare by baseline + x-overlap
    (`SameSpot`). Symbol / MT Extra PUA pieces → one `(` `)` `[` `]` `{` `}` per run of the same family,
    U+F8E7 / U+23AF extender → `/` (`SymbolPiece`). U+0002 between letters → `-`; U+2010–2012 → `-`.
    `ReadPage` maps `¥` (U+00A5) → `×` when `FPDFTextGetFontInfo` says the font is symbolic or named
    *Pi/Symbol/Math/Extra* (a maths-pi font read through the Latin-1 fallback) — no fixture can make
    PDFium produce that, so it's untested.
  - **Equations** (`PdfChunkText.FindEquations`): a line ending in `(8.94)` / `(O.7)` / `(6.2.a)`, label
    in the right 45 % of the page, the rest of the line not prose (< 2 words of 4+ lowercase letters),
    plus contiguous lines whose centre is within 1.6 label heights. `Stacked` when parts' centres differ
    by > ½ label height — PDFium emits a fraction's numerator / denominator as separate lines with no bar,
    so the linear text is wrong. Each becomes a `RagEquation` (label, page, `bbox` = [l, t, r, b] points
    from the CropBox top-left, raw text, `quality` `check`/`suspect`, issues) on every chunk whose body
    holds the label. `PdfTextQuality.ScrambleReason`: stacked, dangling operators (`≥+`, `= +`, `()`),
    a repeated subscript token + mostly 1–2-char tokens, or ≥ 10 tokens that are mostly fragments.
  - **New chunk issues**: `duplicated_glyphs` (a letter 3+ times in a token — `fccc`, `MEEEE`; Roman
    numerals / `www` exempt) → suspect; `scrambled_equation` (labels + pages) → suspect; `table_flattened`
    (≥ 12 consecutive bare values, with the last "Table n.n" before them) → check. Chunk quality is the
    worst of everything; equations carry their own grade. Not done: re-ordering equation glyphs, OCR to
    LaTeX, real table extraction into the chunk (`PdfTableExtractor` still not wired in).
- **Follow-up fixes (re-export review):**
  - **Duplicate check is page-wide** (`kept`: code point → boxes), not the last 12 glyphs of the line —
    an overprint / fake-bold pass PDFium reports as a separate line slipped through. IoU ≥ 0.5. The
    ff-ligature exception now needs the second `f` *straight after* the first (`lastKept`), so a later
    pass's `f` is still dropped.
  - **C0 control characters**: flat (≤ 45 % of the last letter's height) and between letters/digits →
    `-`; anything else → `U+FFFD` (visible, counted in `unmapped_glyphs`) — they're unknown ligatures /
    symbols, an invisible `\x03` helped nobody.
  - **Equation labels need a dot** (`(8.94)`, `(O.7)`, `(6.3N)`, `(6.2.a)`) — `(3)` paragraph numbers
    were 65 false positives — and aren't accepted after a digit/dot (`4.3.2(1)`). Any match on the line
    counts if it's the **rightmost glyph group** on that line, in the right 45 % of the page and **≥ 1.5
    label heights clear** of the glyph to its left (a prose reference has a word space). So a label the
    stream draws *before* its equation is found too (the text then reads "(6.3N) vmin = …"); the
    regex lets letters touch the label because PDFium doesn't always insert a space before a far-off one.
    Chunks match labels whitespace-insensitively. Annex O sample: 25 / 25 located (was 23).
  - **Contents pages** (`IsContentsPage`: ≥ 5 dotted leaders, or ≥ 5 and ≥ 40 % of lines "words … lone
    page number") aren't chunked, and markers on them are dropped. Marker titles lose a trailing page
    number (`TrailingPageNumber`). `titleByNumber` keeps every dotted-numbered marker's title, so a
    **body heading** paragraph (`BodyHeading`: "6.5.5. Reinforcement…", < 120 chars) whose outline entry
    points elsewhere still starts its section. `ClauseHeadingShape` accepts "6.5.5." (number + dot) so
    such a heading is its own paragraph rather than joined to the next line.
  - **Exports default to the PDF's own folder** (`InitialDirectory`, chunks + measurements CSV).
- **Second follow-up:** labels allow **exactly one dot** between numbers (`(5.3.2)` is EN 1992-2's margin
  pointer to the EN 1992-1-1 clause it replaces — 96 false labels) plus a book's own `(D6.17)`
  (`[A-Z]{1,2}\d{0,2}` prefix); the label gap is ≥ 0.8 label heights (a real word space is ~0.25 em).
  More Symbol PUA: radical / arrow / integral / brace extenders (`F8E5 F8E6 F8F4 F8F5`, `23AA 23AE`) → no
  text *and they don't break the bracket run* they lengthen; `F8E8/F6DA` ®, `F8E9/F6D9` ©, `F8EA/F6DB` ™.
  A matched heading's section title is **the printed heading line** (`PrintedHeading`: ≤ 120 chars, not a
  sentence past its number) rather than the outline wording, which may carry a contents page number.
- **`PdfFolderSearch` totals are updated under one lock** — `Interlocked` on each separately let the
  last file's report pair "3 of 3 files" with a match total another worker hadn't added yet (a ~1-in-10
  flaky test once the new PDFium changed the timing).
- App: `DocumentTabViewModel.ExportRagChunksCommand` (async — `Task.Run(Chunk + WriteJsonl)`),
  toolbar "Export chunks…". Batch/folder mode and catalog persistence are not built yet. Not done
  yet: page-image references for suspect chunks, tables as rows (`PdfTableExtractor` not wired in).

## HTML export (`PdfHtmlExporter`, stage 1 of 3)

- **Purpose: an agent reads the standard to build / check design tools.** Text is cleaned exactly as for
  the chunk export; **equations, tables and figures are PNG crops of the page** (the authoritative
  rendering) with the text layer beside them for searching. Stage 2 (not built): tables as real
  `<table>`s with confidence, `<sub>`/`<sup>` in prose from glyph size + baseline. Stage 3 (optional):
  maths OCR → MathML on the crops.
- **Shared pipeline**: `PdfRagChunker.Analyse(doc)` → `DocumentAnalysis` (cleaned `ChunkPage`s, script
  profile, boilerplate, contents pages, section markers, equations) and `BuildFlow(analysis, regionsFor)`
  → `FlowItem`s (paragraph with section + `IsHeading`, or a `PageRegion` with the text-layer lines it
  covered). The chunker calls `BuildFlow(a, null)` — **no regions, output unchanged**; the HTML export
  passes `PdfPageRegions.Detect`. `Segments` assigns a line to a region by its centre (or as the
  region's caption / caption continuation) and places the region where its first line was.
- **`PdfPageRegions.Detect(page, ReadGraphics(doc, p))`**: graphics = bounds of every non-text page
  object (`FPDFPageObjGetBounds`), minus page-sized frames and header/footer-band rules. Captions =
  non-margin lines matching `^(Table|Figure) <num>` + dash/colon + a capital (so "Table 3.1 gives…"
  isn't one); a wrapped caption takes following lines that start lower-case within ~½ line. **Table**:
  the graphics cluster seeded within 45 pt *below* the caption and grown by touching (4 pt), never
  past the next caption; else above; else (unruled) `TextBlockBelow` — closely spaced lines (gap ≤ 2.2
  line heights) until prose / an equation / a caption. **Figure**: cluster above; else `AboveKey` —
  the nearest graphics up to 300 pt above when every line between is key-like (no 8+-word sentence
  ending "."/":"), and the key lines go into the crop; else below. Text poking ≤ 2 pt outside the
  cluster joins it. Equations come from the chunk text's located equations, dropped when inside a
  table/figure.
- **Output**: `<h2-6>` from heading depth (`HeadingDepth`; outline-matched headings plus
  `IsStandaloneHeading` — a short dotted-number line with a real word, so not "1.35 Gk"), `<p>` with
  `class="note"`, `data-quality="suspect"` (unmapped / repeated letters, reason in `title`) or
  `"check"` (maths), page anchors `id="page-N"` with `data-pdf-page` / `data-page-label`,
  `<figure class="equation|table|figure" id="eq-8.94|table-11.1|figure-8.21" data-label data-pdf-page
  data-bbox="l,t,r,b">` (points from the CropBox top-left) + `<img>` + `<figcaption>` + `<details
  class="text-layer">`; tables also get `<details class="extracted"><table data-verified="false">` from
  `PdfTableExtractor` (≥ 2×2 only). A "How to read this export" block explains the conventions.
  Images go to a `{name}_files/` sidecar (`p0146-eq-8.94.png`) or, with `EmbedImages`, data URIs.
- **`RenderRegionPng`**: `PdfCoordinates.PageToDevice` (rotation + crop origin) at `ImageDpi` (200) with
  3 pt padding → `PageRenderer` with a `PageRenderRegion` → **`PngWriter`** (own encoder, `ZLibStream`,
  Up filter; greyscale when every pixel is grey — most of a standard). App: Tools ▾ "Export HTML…"
  (`DocumentTabViewModel.Html.cs`, wait cursor, `Task.Run`, summary dialog).
- **Equation crops are the display block, not the char boxes** (review: 38 / 571 clipped — radicals,
  fraction bars and big brackets are vector lines or glyphs without a usable box). `Detect` takes the
  band from 1.5 pt under the nearest *text line* above the label (`IsTextLine`: prose, a "where /
  with / for…" line, a "(n)" paragraph, a heading, a caption, or anything in the header/footer band) to
  1.5 pt over the nearest one below, ≤ 100 pt from the label and never into the margin band, across the
  text column (`TextColumn`: 10th / 90th percentile of long body lines' edges); never smaller than the
  text layer's own box. Two stacked equations split at `Separator` — the middle of the widest blank gap
  in the ink (glyph lines + graphics) between their labels (`ChunkEquation.LabelBounds`), so (B.11)'s
  numerator no longer lands in (B.10). **Equation crops render with no padding** (the block already
  holds the whitespace); tables / figures keep 3 pt.
- **`InkAtEdges`** checks every rendered crop: dark (< 140) pixels on the top / bottom row (left 85 %)
  or the left column → `crop_clipped` issue + `data-quality="suspect"` (`HtmlExportResult.ClippedImages`).
  It caught the first version of the block crop running into the licence footer.
- Region text layers drop margin / boilerplate lines and `StripLicence` the rest (128 watermark hits in
  the review). Table header text the column split cut through ("A" | "nchorage l" | "ength lbd/" | "ϕ")
  is re-joined with `colspan` (`MergeSplitCells`: previous cell ends in a letter / "/", next starts
  lower-case or follows "/"). Every figure carries `data-authority="image"`; text-layer summaries say so.
- `BlockStart` now also opens a paragraph at an annex number + capital ("O.7.1 General") — a long
  "O.7 …" heading was otherwise joined with the next heading (affects chunks too, for the better).

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
- **`BuildLinkOverlays` runs on every scroll event, so it must not replace an unchanged page's
  `Links`.** It memoises per slot (`PageSlotViewModel.LinkOverlayKey` = the cached link / cross-ref
  lists by reference + scale, unrotated size, rotation, crop) and skips a page whose inputs match.
  Replacing the list made WPF recreate every link `Button` on the page each tick — a contents page
  with hundreds of links stuttered scrolling (`LinkOverlayTests`). Empty inputs use `Array.Empty`
  so "still loading" keys stay stable too.
- **`PdfLinks.Read` also merges bare URLs** found by PDFium's text web-link detector
  (`FPDFLinkLoadWebLinks` → `FPDFLinkCountWebLinks` / `GetURL` / `CountRects` / `GetRect`, inside
  `PdfTextExtractor.WithTextPage`). One `PdfLink` per on-page rect; web links overlapping an
  existing `/Link` are dropped; a page with no text layer is skipped (`PdfException` swallowed).

## Extra PDFium bits

- **Page labels** — `PdfDocument.GetPageLabel(i)` / `HasPageLabels` (`FPDF_GetPageLabel`, cached
  like `_pageSizes`, cleared on structural edits; returns null when the label equals the
  ordinal). App: `PageDisplay.Label(doc, i)` = label-or-ordinal, used by the pane `PageCountText`,
  the Bookmarks / Clauses / Annotations nav rows (each takes a `Func<int,string>` /
  `DocumentTabViewModel.PageLabelFor`), and `Citations` (page arg is now a `string`).
- **Trim margins** — `PdfDocument.TrimMargins(pages, marginPt=6, progress, ct)` renders each page
  (~120 DPI, `PageRenderer`), finds the non-white bbox, maps it back via
  `PdfCoordinates.DeviceToPage`, and stores an absolute `PageRef.CropBox`. `Rebuild()` re-applies
  crop after rotation (like `Rotation`); the first trim `Rebuild()`s so a source page's box is
  never mutated (undo can't reverse `FPDFPage_SetCropBox` in place — an undo that changes any
  crop rebuilds from source). Returns the count trimmed. App: toolbar "Trim margins" →
  `DocumentTabViewModel.TrimMarginsCommand` (wait cursor, `Task.Run`, confirm > 40 pp).
- **`PdfDocument.IsTagged`** (`FPDFCatalogIsTagged`) → the "Format" line in Document Properties.
  Investigated `fpdf_structtree` for clause detection and shelved it (real "tagged" engineering
  PDFs auto-tag everything `H1` or carry no heading elements) — see the spec.

## Testing

`dotnet test`. Fixtures are generated in-process by `TestPdfBuilder` (a minimal PDF writer) —
no binary files in the repo. Test parallelism is disabled (native interop). `PdfAnnotations.Read`
re-reads from the live handle, which the `PdfDocument` ctor has already stripped — so tests must
assert with `doc.GetAnnotations(i)` (the model), not `PdfAnnotations.Read` after a `Load`.

Set `DOCDR_BINDING_LOG=<path>` to have the app log WPF data-binding errors to that file
(`App.OnStartup`). Gotcha: a child view that sets its own `DataContext` (e.g. `PdfPaneView
DataContext="{Binding RightPane}"`) resolves its *other* bindings against that new context —
use `RelativeSource AncestorType=...` to reach the parent VM.

## Diagnostics & versioning

- **Version** is set once in `Directory.Build.props` (`<Version>`), repo-wide. `InformationalVersion`
  gets a `+<short git sha>` suffix via the `ShortenGitSha` target in `DocDr.App.csproj` (the SDK
  fills the full 40-char hash; the target trims it to 10). `DiagnosticsLog.Version` /
  `.InformationalVersion` expose it; it shows in the window title, the home screen, and
  **Help → About** (the version button at the right of the status bar → `MainViewModel.AboutCommand`).
- **`DiagnosticsLog`** (`DocDr.App/Services`) is the only always-on log: `%APPDATA%\DocDr\logs\docdr-<date>.log`,
  one session-start line + any unhandled exception. `App` wires `DispatcherUnhandledException`
  (log → "keep open?" prompt → `e.Handled`), `AppDomain.UnhandledException` (log → dialog if
  terminating) and `TaskScheduler.UnobservedTaskException` (log + `SetObserved`). Native PDFium
  crashes still bypass all of this — a bad file can hard-kill the process.

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
`BasedOn="{StaticResource {x:Type X}}"`. **The .NET 9 Fluent `MenuItem` template references
`CheckBoxBackground` / `CheckBoxBorderBrush` without defining them** — `Palette.Shared.xaml`
supplies both (transparent) so opening a menu doesn't log "Resource not found". **The Fluent
`TextBox` always shows a clear-✕ while focused with text** (no property to disable it); `App.OnStartup`
calls `TextBoxClearButton.HideInMultilineBoxes()` — a class-level `TextBox` `Loaded` handler that
collapses the template's `ClearButton` (a local `Visibility` beats the template triggers) in
`AcceptsReturn` boxes only; single-line boxes (find bar) keep it. Not an App-level implicit style:
`BasedOn="{StaticResource {x:Type TextBox}}"` in App.xaml can resolve before Fluent is merged. Fluent's default Button padding clips a fixed-width
icon button (see `GlyphButton` in `PdfPaneView.xaml`).
