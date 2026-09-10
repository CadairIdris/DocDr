# Small devs to work through


Allow comments to be shown in floating boxes

Occasional blank page rendering issues in continuous mode


find via ctrl+f in floating box - optional regex search
find all shows table view


We are a design code / official document parser and navigator?
Integrate national annexes?

table export - a little dodgy



Manually add links to other docs


Bake the pdf - print the annotations to the doc so cant be edited - good for collage.


## Open email (.msg / .eml) and convert to PDF

People archive email threads as PDF — fits the "official document parser/navigator"
angle. New feature, ~1 week for solid / 2-3 days for a rough happy path.

- New `DocDr.Mail` project (keep the mail deps out of `DocDr.Pdf`). `MsgReader`
  (NuGet, maintained) parses both `.msg` (OLE2 compound) and `.eml` — headers,
  HTML/RTF/text body, attachments, embedded `cid:` images. `.eml` alone would just
  be MimeKit.
- Body -> PDF via WebView2 `CoreWebView2.PrintToPdfAsync` (PDFium can't lay out
  HTML). Build an HTML doc = header table (From/To/Cc/Date/Subject) + body, with
  `cid:` images rewritten to data URIs.
- Wire into the app: file dialog accepts `.msg`/`.eml`; `OpenPathAsync` detects the
  extension, converts to an in-memory PDF, opens it as an untitled doc (same path
  as `Merge` output). Save writes the PDF.
- Attachments: PDFs appended via existing `PdfDocument.Merge`; images inline;
  everything else -> a trailing "Attachments" list page or saved alongside.
- Risks: WebView2 runtime dependency (fine on Win11, needs a fallback check);
  WebView2 async + offscreen environment is a bit awkward from a background
  convert; `.msg` embedded-image resolution is fiddly.


## Sharp rendering at high zoom (viewport render)

DONE for reading zoom (branch `fix/render-sharpness`: greyscale AA, DPI-baked
1:1 blit). Past ~3x zoom the `MaxRenderEdge` cap downscales the page bitmap and
WPF upscales it -> blur. Fix: above a zoom threshold, rasterise only the visible
slice of the page at exact device resolution and position it; re-render on
scroll / zoom-settle. Also removes the extreme-zoom OOM risk.


##  Open other files to pdf

dxf drawings
