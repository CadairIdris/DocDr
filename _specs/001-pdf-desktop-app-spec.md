# PDF Desktop App — Staged Build Specification

## 1. Overview

A desktop PDF viewer, editor, and cataloging tool for Windows, targeting efficiency over
feature breadth. Built in stages so each stage produces a usable, testable increment rather
than one large build.

**Core capabilities (in scope):**
- View PDFs in single-page, continuous, and grid (multi-column, auto-fit) modes
- Collapsible navigation panel per tab: page thumbnails and the document's bookmark
  outline, either one for jumping around the document
- Tabbed workspace: multiple documents open at once, each in its own tab; a document
  can also be opened in more than one tab (independent view of the same file)
- Two independent views into the same document, each at a different page, each with its own search
- Rotate, insert, and delete pages
- Highlight text and add comments, with threaded replies on comments
- Print the current document (with edits and annotations applied)
- Browse a folder tree of PDFs
- View and edit PDF metadata
- Find duplicate/near-duplicate PDFs across a large collection
- Build a searchable SQLite catalog of a large PDF library

**Explicitly future / out of scope for initial build:**
- OCR of scanned/image-only PDFs (Stage 6, deferred)
- Drawing, comparison & review tools — dimension lines with snapping, quick measure, PDF
  overlay, side-by-side diff, markups schedule (Stage 7, deferred)
- Design-code navigation & extraction — clause tree, clickable cross-references, cite / snip
  with citation, table extraction, cross-document links, revision awareness, saved sessions
  (Stage 8, deferred)
- Local API / headless mode for scripts and AI agents (Stage 9, deferred)
- Format conversion, e-signing, forms, in-app AI chat — not planned (an external agent may
  drive DocDr through the Stage 9 API instead)

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
- A collapsible navigation panel per tab with two sections: a page-thumbnail strip and the
  PDF's bookmark/outline tree (read via PDFium). Clicking a thumbnail or a bookmark navigates
  the active pane to that page.
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
- Print the active document: a standard `PrintDialog` (printer, page range, copies), pages
  rendered to the printer at print resolution via the existing PDFium bitmap pipeline and
  fitted to the sheet. Prints the current in-memory state — i.e. from `SaveToBytes()`, so
  page edits (Stage 2) and annotations (Stage 3) are included. Raster output is acceptable;
  the paginator renders pages on demand so large documents do not blow memory.

**Acceptance criteria:**
- Opening a multi-hundred-page PDF remains responsive while scrolling in continuous mode.
- Printing a selected page range produces output matching the on-screen pages, including any
  annotations, at a sensible resolution.
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
- Threaded replies on comments: a comment can carry a chain of reply messages, each with its
  own author and timestamp. PDFium has no setter for an indirect-reference dictionary entry,
  so DocDr cannot emit a standard `/IRT` reply-annotation chain; instead the whole thread is
  stored on the parent annotation as one private-key string, which round-trips losslessly
  through PDFium (and through any reader that preserves unknown annotation keys) while other
  readers still show the opening comment. The comment editor presents the thread as a message
  list with a reply box; the navigation panel shows the reply count.
- View, edit, and delete existing annotations of both types.
- Persist annotations into the saved PDF in standard annotation format (so they remain
  visible in other PDF readers).

**Acceptance criteria:**
- Annotations created in this app are visible and correctly positioned when the same file
  is opened in another standard PDF reader.
- Annotations survive a page rotate/delete/insert operation on unrelated pages without
  shifting to the wrong page.
- A comment with replies, saved and reopened in DocDr, keeps the full thread in order with
  authors and timestamps intact; in another reader the opening comment still shows.

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
- **RAG chunk export:** an "Export → RAG chunks (JSONL)" action (single document or a
  catalog selection) that splits extracted body text into retrieval-sized chunks.
  - Split section-by-section using the bookmark outline (`PdfBookmarks`) for boundaries and
    section titles; within a section, sub-split into ~500–800 token windows (token count
    approximated as characters / 4) with ~15% overlap between adjacent chunks.
  - Strip running headers/footers before chunking: short lines at a consistent vertical
    position that recur on most pages (detected from character rectangles), plus bare page
    numbers. Join wrapped lines into paragraphs and de-hyphenate across line breaks.
  - Watermark stripping (Stage 4) runs first so repeated watermark text is not embedded.
  - Each JSONL record: `text`, `source_path`, `doc_title`, `page_start`, `page_end`,
    `section_title`, `chunk_index`. Persist chunks in a catalog table keyed by document ID
    alongside the export.
  - Documents (or pages) with no extractable text layer are reported and skipped — full
    coverage waits on Stage 6 OCR.
  - **Batch mode:** point the export at a folder tree (or a catalog selection) and chunk
    every PDF under it — a queued background job with progress, a per-file error list, and
    resume on re-run (skip files already chunked and unchanged). Output is one JSONL per
    source file, or one combined file, at the user's choice. This is a thin wrapper over the
    single-document path above; it is only worth building once that path is solid, and a real
    run over a mixed folder will surface many no-text-layer files (so it pairs with Stage 6).
  - Out of scope: generating embeddings, multi-column reading-order recovery, table
    structure extraction.

**Acceptance criteria:**
- Catalog build completes over a large existing library (thousands of files) in a reasonable
  time and remains queryable while ingest is still running, if feasible, or reports clear
  progress if not.
- Search returns correct results ranked sensibly against a realistic mixed-content library.
- Duplicate groups correctly separate exact-byte duplicates from near-duplicates and avoid
  false-positive grouping of unrelated documents.
- RAG chunk export on a text-layer PDF (e.g. a Eurocode) produces JSONL whose chunks carry
  the correct section titles and page ranges, contain no repeated header/footer lines, and
  stay within the target token window.

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

---

### Stage 7 — Drawing, Comparison & Review Tools (Future)

**Goal:** Measurement, comparison and review-tracking features aimed at construction /
engineering drawings — where the page is vector linework at a known scale rather than flowing
text — plus a markups schedule that also serves document review generally.

**Shared foundations (build once; the measurement and comparison features reuse them):**
- **Page vector model:** walk every path object on a page via PDFium (`FPDFPageGetObject` /
  `FPDFPageObjGetType` == path / `FPDFPath*` segment accessors), transform each segment to
  page space through the object + page matrices, and index the resulting line segments in a
  spatial grid. Vector-only — a page with no path geometry (a scan) falls back to free
  placement / whole-page compare, and the UI says so. Confirm the `FPDFPathGetPathSegment` /
  `FPDFPathSegmentGetPoint` bindings exist in the chosen wrapper before relying on this.
- **2-point registration:** a small modal where the user clicks the same feature on two pages
  (this document's page, or an overlay document's page); DocDr solves the translation + uniform
  scale that maps one onto the other. Used by overlay and visual diff so mis-exported or
  scanned pairs still line up.
- **Scale calibration:** the user draws along a dimension of known real length and enters it
  (e.g. "5000 mm"); DocDr stores points-per-unit for that page (or document) and a display
  unit. Re-viewable and re-settable. Used by the dimension tool and any later measurement.

**Requirements:**
- **Dimension line annotation:** click two points (drag with a live preview, same overlay
  machinery as the Stage 3 shape tools); DocDr draws extension lines, a dimension line with
  arrowheads/ticks, and the measured length as text, computed from the page's calibration.
  Snap the endpoints to the page vector model — nearest segment endpoint, on-segment point,
  or segment intersection within a tolerance — with a snap indicator; no snap on a page with
  no vector geometry. Authored as a stamp-backed annotation like the other shapes, so it
  prints and round-trips.
- **Quick measure (no annotation):** a ruler / area tool that shows a live length or polygon
  area readout in the calibrated unit as you move the cursor, using the same snapping, without
  committing anything to the page. The lightweight companion to the dimension annotation.
- **Overlay two PDFs:** a pane view mode that renders the current page and the matching page
  of a chosen second document, tints one green and one red, and composites them. Works
  directly for revisions exported from the same source; 2-point registration handles pairs
  that do not already align. Blend mode (multiply / difference) selectable.
- **Side-by-side diff:** extend split view so the second pane can hold a different document
  with an optional scroll/zoom lock. On top of that:
  - **Text diff** (specs / contracts): extract text from both sides, run a line/word diff,
    map the changed runs back to on-page rectangles (via the character boxes) and highlight
    added / removed / changed text in each pane.
  - **Visual diff** (drawings): the overlay above in difference-blend mode, plus bounding
    boxes drawn around each connected region of change.
  - **Page-level diff:** flag inserted / deleted / reordered pages by comparing per-page text
    (or render) hashes.
- **Markups schedule (review workflow):** every annotation gains a review status
  (`open` / `responded` / `closed` / `superseded`), an optional discipline / category, and a
  priority. A "Markups" panel lists them all with filter / sort / group (by status, page,
  author, discipline) and jumps to each. Export the set as a schedule (CSV / Excel) or a
  stamped PDF with a summary sheet. This is DocDr's equivalent of Bluebeam Revu's *Markups
  List* + basic *Studio*-style review tracking; it deliberately stops short of Studio's
  live multi-user sessions and cloud sync (single-file / catalog-backed only). The status and
  metadata ride along in the annotation's private key like the reply thread does.

**Out of scope:** angular / radius measurement (dimension line is linear only for v1),
CAD-style object snapping beyond endpoint/on-line/intersection, editing the other document
from the diff view, three-way / merge, real-time multi-user markup sessions.

**Acceptance criteria:**
- On a vector drawing (e.g. an RC details sheet), a dimension line snapped between two
  gridlines reports a length matching the drawing's stated dimension within rounding, and the
  annotation survives save / reload and prints.
- Overlaying two revisions of the same drawing shows unchanged linework in a neutral blend and
  the differences clearly in each colour; a deliberately mis-scaled pair aligns after 2-point
  registration.
- A text diff of two revisions of a specification highlights exactly the changed clauses in
  both panes.
- A review with a dozen markups across several pages exports to a schedule whose rows carry
  the comment text, page, author, date, status and any reply, and re-importing / reopening the
  PDF restores every status.

---

### Stage 8 — Design-Code Navigation, Extraction & Reference Workflow (partially done)

**Goal:** Make DocDr genuinely good at the daily engineering task of reading design codes and
reference material — navigating dense clause-numbered documents, pulling data out of them, and
building a searchable personal knowledge base of commentary and cross-references.

**Feature bundle — clause-aware navigation (DONE, `master`):**
- **Clause index / tree (DONE):** `PdfClauses.Read` detects clause numbering from PDFium's
  reading-order text (`6.4.3`, national-annex / appendix variants like `A.2.1`, bare chapter
  and `Annex X` headings), builds a per-document tree, and shows it as a Navigator tab that
  jumps to any clause. Background scan on open. Tuned for dotted-decimal (Eurocode / BS EN /
  ISO); letter-section schemes (AISC `D1.2a`) are not covered. *Not done: current-clause
  status-bar indicator while scrolling.*
- **Clickable textual cross-references (DONE):** `PdfCrossReferences.Scan` turns cued in-body
  clause references ("see 6.2.5", "in accordance with 8.3.1"), annex references ("Annex L"),
  and figure / table references ("Figure 8.5", "Table 4.3") into underlined clickable links,
  merged into the Stage 1 link overlay. Clause refs resolve against the clause index (walking
  up the dotted prefix); figure / table refs resolve against a caption map built in the same
  scan pass (exact match only — no wrong jumps). Formula references are not linked.
- **Cite this (DONE):** right-click a clause in the index → "Copy citation"
  (`{file}, cl. 6.5 (Concrete cover), p. 88`); or select text on a page → "Cite" in the popup
  copies the quoted passage with source, nearest detected clause, and page. Currently keyed off
  the filename — will use catalog metadata once Stage 5 lands.

**Snip to clipboard with citation:** drag a box over any region of a page (a figure, a detail,
a table) and copy it to the clipboard as an image together with an auto-generated caption
naming the source — document title, page, and clause/figure/table number when detectable.
One-keystroke capture for reports and emails.

**Table extraction:** draw a box over a table; reconstruct rows and columns from the character
boxes plus any ruling lines and copy to the clipboard / export as CSV. Linear code tables
(material properties, partial factors, section data) are the target — merged cells and nested
headers are best-effort. Equation → LaTeX/MathML is explicitly out of scope for this stage.

**Cross-document / clause links (knowledge base):** typed links stored in the catalog between
annotations, documents, and clauses — e.g. a note in a calc "relates to" a clause in EN
1992-1-1, which "is superseded by" a clause in the 2023 edition. Browse the links from either
end; surface them in search results. This is the connective tissue that turns the Stage 5
catalog into a personal engineering reference base.

**Revision / withdrawal awareness:** mark a document (or a specific edition of a code) as
superseded-by another in the catalog; warn on opening a withdrawn edition and offer to open
the current one. Using a withdrawn code is a real liability, so this is a guardrail, not just
a convenience.

**Restore review session:** save and reopen a named workspace — which documents are open, at
which page / zoom / view mode, the split layout, the nav-panel state and any active markup
filters — so a multi-week piece of work survives closing the app.

**Acceptance criteria:**
- Opening a Eurocode part builds a clause tree that matches its printed numbering, and a
  "see 6.2.5"-style reference in the body navigates to clause 6.2.5. *(met — verified on
  BS EN 1992-1-1:2023)*
- Snipping a code table and pasting into a spreadsheet gives usable rows/columns, and the
  clipboard caption names the code, edition, page and table number.
- A note linked to a clause is reachable from the clause and appears when that clause's text
  matches a search.
- Opening a withdrawn code edition that has a newer edition in the library shows a warning.

---

### Stage 9 — Local API / Headless Mode (Future, cross-cutting)

**Goal:** Expose DocDr's document capabilities to the user's own engineering software, scripts
and (later) AI agents — turning it from an application into local infrastructure. This is a
thin transport layer over capabilities delivered by earlier stages; it can grow incrementally
alongside them rather than being a single milestone.

**Requirements:**
- A local endpoint (HTTP on loopback, and/or a CLI) — no external network, no auth beyond the
  local machine — offering read operations over: catalog search (FTS + structured fields),
  document and clause text extraction, page rendering to an image at a requested DPI,
  annotation / markup listing, and RAG chunk export (Stage 5).
- Write operations are limited and explicit: add an annotation / note, add a catalog tag or
  cross-document link. No structural PDF edits over the API in v1.
- Responses are plain JSON (plus image bytes for renders); every returned text span carries
  its document id, page and, where known, clause number, so a caller can cite what it used.
- Designed with an AI agent as a first-class consumer: the same operations a person does in
  the UI (find the governing clause, read it, draft a review comment, summarise the changes
  between two revisions) are expressible as API calls.

**Acceptance criteria:**
- A script can search the library, fetch the text of a named clause with its citation, and
  render that clause's page to a PNG, using only the local API.
- Adding a note via the API is visible in the app on the next open and round-trips on save.

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
- RAG chunk export (Stage 5) — confirm target token window, overlap fraction, and whether an
  exact BPE tokenizer is needed over the characters/4 approximation; decide whether export is
  a one-off action or a standing part of catalog ingest.
- Page vector model (Stage 7) — verify the chosen wrapper binds the PDFium path-segment
  accessors (`FPDFPathGetPathSegment`, `FPDFPathSegmentGetPoint`, `FPDFPathSegmentGetType`)
  before committing to snapping / vector diff; if not, that whole stage needs a different
  approach.
- Dimension calibration (Stage 7) — per-page vs per-document scale; whether to auto-detect a
  scale from a drawing's scale bar / title-block text, or always ask.
- Overlay / diff registration (Stage 7) — is translation + uniform scale enough, or is
  rotation / non-uniform scale needed for real drawing pairs.
- Recommended build order for Stage 7: dimension line + calibration (no snap) → assume-aligned
  overlay → side-by-side + text diff → snapping and 2-point registration as follow-ups; the
  markups schedule is independent and can land any time after Stage 3.
- Markups schedule (Stage 7) — schedule export format (CSV vs real .xlsx vs a stamped summary
  PDF), and whether status/discipline are a fixed vocabulary or user-configurable.
- Clause-number detection (Stage 8) — how much to hard-code per code family (Eurocode, BS,
  ASTM/AISC, ICE) vs a general numbering grammar; confirm against a sample of the user's own
  codes before relying on it.
- Table extraction (Stage 8) — line-based (use ruling rectangles) vs whitespace-based column
  detection, and how much merged-cell / multi-row-header handling is worth it for v1.
- Stage 9 API surface & transport — HTTP-on-loopback vs CLI vs both; whether it runs in-process
  with the app or as a separate lightweight host over the same DocDr.Pdf / catalog libraries;
  the exact operation list. Build the smallest useful slice (search + text + render) first.
- Recommended stage order overall: 5 (catalog) unblocks 8's knowledge base and 9's search;
  6 (OCR) unblocks batch chunking and scanned-drawing tools; 7's markups schedule and 8's
  clause navigation / snip-with-citation are the highest day-to-day value and only need
  Stages 1–3.
