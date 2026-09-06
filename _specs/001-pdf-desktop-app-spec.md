# PDF Desktop App — Staged Build Specification

## 1. Overview

A desktop PDF viewer, editor, and cataloging tool for Windows, targeting efficiency over
feature breadth. Built in stages so each stage produces a usable, testable increment rather
than one large build.

**Core capabilities (in scope):**
- View PDFs in single-page, continuous, and grid (multi-column, auto-fit) modes
- Thumbnail navigation panel (collapsible) for jumping around a document
- Tabbed workspace: multiple documents open at once, each in its own tab; a document
  can also be opened in more than one tab (independent view of the same file)
- Two independent views into the same document, each at a different page, each with its own search
- Rotate, insert, and delete pages
- Highlight text and add comments
- Browse a folder tree of PDFs
- View and edit PDF metadata
- Find duplicate/near-duplicate PDFs across a large collection
- Build a searchable SQLite catalog of a large PDF library

**Explicitly future / out of scope for initial build:**
- OCR of scanned/image-only PDFs (Stage 6, deferred)
- Format conversion, e-signing, forms, AI features — not planned

## 2. Architecture

- **UI**: WPF (.NET)
- **PDF engine**: PDFium (Google's rendering/manipulation library), via prebuilt binaries
  from the `pdfium-binaries` project — do not build PDFium from source.
- **.NET/PDFium bridge**: use an existing wrapper rather than raw P/Invoke.
  Evaluate **PDFiumCore** (actively maintained, generated bindings, broad platform support)
  and **Morph.PDFium** (higher-level API: rendering, text extraction/search, annotations,
  page manipulation, save) and pick one before Stage 1 begins.
- **Catalog store**: SQLite with an FTS5 virtual table for full-text search, plus normal
  tables for file metadata, tags, and duplicate-group records.
- **Future OCR engine**: Tesseract via a .NET wrapper, fed directly from PDFium-rendered
  page bitmaps.

### Cross-cutting technical constraints

- **Rendering**: render pages into a `WriteableBitmap`, not `System.Drawing.Bitmap` →
  `BitmapSource` — materially faster for interactive zoom/scroll.
- **Threading**: PDFium is not safe for concurrent access. In practice the `pdfium-binaries`
  build is not safe even across *different* documents (shared font cache / allocator state),
  so **every** PDFium call — whichever document or tab it belongs to — is serialized through a
  single process-wide lock. Do not attempt lock-free or per-document concurrent rendering.
  Rendering still runs off the UI thread (one background worker behind the lock).
- **Tabbed shell**: each tab owns one `PdfDocument` and two pane view models; a document opened
  in multiple tabs is loaded once per tab (simplest correct model — no shared mutable handle
  across tabs). Reference-count nothing; a tab close disposes its own document.
- **Metadata writes**: reading PDF Info-dictionary metadata via PDFium is standard and easy.
  Writing metadata back into the PDF is less consistently supported across PDFium wrappers —
  confirm the chosen wrapper actually supports it during Stage 4. Regardless of what the PDF
  itself supports, **the SQLite catalog is the source of truth** for tags/categories/notes;
  writing back into a PDF's own metadata is a nice-to-have, not a dependency for search to work.
- **Licensing**: PDFium (BSD-style), any chosen .NET wrapper — confirm each is permissively
  licensed (Apache 2.0/MIT/BSD) before adoption. Avoid AGPL/GPL-licensed PDF libraries
  (e.g. MuPDF without a commercial license, Poppler) given this is not intended to be
  open-sourced itself.

## 3. Staged Build Plan

---

### Stage 1 — Core Viewer + PDFium Plumbing

**Goal:** A working viewer proving the hardest architectural piece (dual synchronized views)
before investing in anything else.

**Requirements:**
- Load a PDF via PDFium and render pages to WPF via `WriteableBitmap`.
- View modes per pane: single-page, continuous (scrollable), and grid — pages wrapped into
  rows, column count auto-fitting the pane width at the current zoom (zoom out ⇒ more columns).
- A collapsible thumbnail strip per tab; clicking a thumbnail navigates the active pane.
- Split view is optional and off by default (one pane per tab); a toggle reveals the second
  independent pane on the same document.
- The two panes (when split) can each be scrolled/paged to a different location independently.
- Per-view text search: find all matches, highlight them, jump between matches, independent
  state per view (i.e. searching in view A does not affect view B's position or find state).
- Basic zoom and page navigation controls.
- Tabbed shell: the window hosts a tab strip; each tab is one open document with its own
  two-pane view and its own per-pane state. Opening a file adds a tab; the same file may be
  opened in multiple tabs. Tabs can be closed independently, releasing that document unless
  another tab still holds it.

**Acceptance criteria:**
- Opening a multi-hundred-page PDF remains responsive while scrolling in continuous mode.
- Two views on the same document can be at different pages simultaneously with correct,
  independent rendering and no crashes or corruption from concurrent PDFium calls.
- Search in one view never affects the other view's state.
- Several documents (and/or several tabs on one document) can be open at once with no
  cross-tab interference and no PDFium corruption; closing a tab frees its resources.

---

### Stage 2 — Page Editing

**Goal:** Rotate, insert, and delete pages using PDFium's native page-manipulation API.

**Requirements:**
- Rotate one or more selected pages (90°/180°/270°).
- Delete one or more selected pages, with confirmation.
- Insert page(s) from another PDF file at a chosen position.
- Reflect edits immediately in the open view(s); support save / save-as.
- Undo for at least the last edit action.

**Acceptance criteria:**
- Edits persist correctly on save and reload.
- Editing operations do not corrupt annotations added in Stage 3 on unaffected pages.

---

### Stage 3 — Annotation Overlay

**Goal:** Highlight text and add comments — the largest custom-UI component in the project,
since PDFium exposes the underlying annotation API but no ready-made editing UI.

**Requirements:**
- Text selection within a rendered page (using PDFium's text/character-box APIs to map
  screen coordinates to text ranges).
- Create highlight annotations over a text selection, with quad points matching the
  selected text's bounding boxes, and a choice of highlight color.
- Create comment annotations (text/popup annotations) attachable to a location or a
  highlighted selection.
- View, edit, and delete existing annotations of both types.
- Persist annotations into the saved PDF in standard annotation format (so they remain
  visible in other PDF readers).

**Acceptance criteria:**
- Annotations created in this app are visible and correctly positioned when the same file
  is opened in another standard PDF reader.
- Annotations survive a page rotate/delete/insert operation on unrelated pages without
  shifting to the wrong page.

---

### Stage 4 — Folder Browser + Metadata Viewer

**Goal:** Navigate a large existing PDF collection and inspect/edit metadata per file.

**Requirements:**
- WPF tree view of the filesystem, filtered/highlighted for PDF files.
- Metadata panel showing Info-dictionary fields (Title, Author, Subject, Keywords, Creator,
  Producer, CreationDate, ModDate) for the selected file, read via PDFium.
- Editable fields for user-managed metadata (tags, category, notes) — stored in the SQLite
  catalog (see Stage 5), independent of whether they can also be written into the PDF itself.
- If the chosen PDFium wrapper supports writing Info-dictionary fields, allow editing those
  too and confirm the write round-trips correctly; if not, surface those fields as read-only.

**Acceptance criteria:**
- Metadata display is correct against a sample set of PDFs with varied/missing metadata.
- User-managed tags/notes persist in the catalog independent of any PDF metadata write support.

---

### Stage 5 — SQLite Catalog + Duplicate Finder

**Goal:** Build a searchable index of a large PDF library and identify duplicates within it.

**Requirements:**
- Ingest pipeline: for each PDF under a chosen root folder, extract Info-dictionary metadata,
  extract full body text, compute a file hash, and compute a duplicate-detection signature.
- SQLite schema: a documents table (path, metadata fields, tags, hashes, page count, ingest
  timestamp) and an FTS5 virtual table over extracted body text, joined by document ID.
- Duplicate detection, two tiers:
  - **Exact duplicates**: SHA-256 hash of file bytes.
  - **Near-duplicates**: normalized-text hash plus page count as a first pass; a perceptual
    hash of the first rendered page as a secondary signal for scanned documents where text
    extraction is weak or absent.
- A duplicates view grouping matched files, allowing the user to review and delete/keep.
- Full-text and metadata search UI over the catalog (query the FTS5 table plus structured
  fields), returning results the user can open directly in the viewer.
- Incremental re-scan: detect new/changed/removed files on subsequent catalog runs without
  re-processing the entire library each time.

**Acceptance criteria:**
- Catalog build completes over a large existing library (thousands of files) in a reasonable
  time and remains queryable while ingest is still running, if feasible, or reports clear
  progress if not.
- Search returns correct results ranked sensibly against a realistic mixed-content library.
- Duplicate groups correctly separate exact-byte duplicates from near-duplicates and avoid
  false-positive grouping of unrelated documents.

---

### Stage 6 — OCR (Future)

**Goal:** Extend the catalog ingest pipeline to index scanned/image-only PDFs that have no
existing text layer.

**Requirements:**
- Detect PDFs (or individual pages) with no extractable text layer during ingest.
- Render such pages via the existing PDFium bitmap pipeline and pass them to Tesseract
  (via a .NET wrapper) for text recognition.
- Feed OCR output into the same FTS5 indexing path used for native text, tagged so the UI
  can indicate a document's text came from OCR (useful for accuracy caveats).
- Consider a background/batch mode given OCR is materially slower than native text
  extraction, so it should not block interactive catalog use.

**Acceptance criteria:**
- Scanned PDFs become searchable via the same search UI as native-text PDFs.
- OCR ingest runs without blocking the UI or the rest of the catalog pipeline.

## 4. Open Decisions for the Agent to Flag Before/During Build

- Final choice between PDFiumCore vs Morph.PDFium (or another wrapper) — confirm annotation
  and metadata-write support meets Stage 3/4 requirements before committing.
- Whether Info-dictionary metadata writing is supported by the chosen wrapper (affects Stage 4
  scope, per the constraint above).
- Threshold/algorithm details for near-duplicate detection in Stage 5 (exact similarity
  threshold, which perceptual hash algorithm) — propose an approach and confirm before
  implementing at scale.
- OCR language support scope (Stage 6) — confirm which languages matter for the target library
  before choosing Tesseract language data packs.
