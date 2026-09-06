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

## Testing

`dotnet test`. Fixtures are generated in-process by `TestPdfBuilder` (a minimal PDF writer) —
no binary files in the repo. Test parallelism is disabled (native interop).
