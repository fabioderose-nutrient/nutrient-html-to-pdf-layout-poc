using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdPicture14;
using HtmlAgilityPack;

// Builds one PDF from three HTML files: a header and a footer repeated on every page, and
// a content page whose largest table is split across as many pages as needed.
//
// Usage: HtmlToPdfLayout [input-folder] [output.pdf]
//   input-folder  holds header*.html, footer*.html and content*.html (default: samples)
//   output.pdf    default: output/result.pdf

// An empty key starts GdPicture in evaluation mode.
LicenseManager licenseManager = new LicenseManager();
licenseManager.RegisterKEY(Environment.GetEnvironmentVariable("GDPICTURE_LICENSE_KEY") ?? "");

// Page format. Flip Landscape to true for landscape A4.
const bool Landscape = false;
float FinalPageWidth  = Landscape ? 842f : 595f;
float FinalPageHeight = Landscape ? 595f : 842f;

// Vertical breathing bands between blocks on the assembled page (in final A4 pt).
const float GapHeaderToContent = 12f;
const float GapContentToFooter = 12f;

// Page padding: empty space ABOVE the header and BELOW the footer (final A4 pt).
const float TopPadding    = 24f;
const float BottomPadding = 24f;

// Light side padding: kept on the final A4 page (final A4 pt).
const float SidePadding = 18f;

string inputDir = Path.GetFullPath(args.Length > 0 ? args[0] : "samples");
string finalPdf = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine("output", "result.pdf"));
string outputDir = Path.GetDirectoryName(finalPdf)!;
Directory.CreateDirectory(outputDir);

// Temp dir for intermediate per-chunk HTML/PDF files.
string tempDir = Path.Combine(outputDir, "_chunks");
if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
Directory.CreateDirectory(tempDir);

string headerHtml = FindHtml(inputDir, "header");
string footerHtml = FindHtml(inputDir, "footer");
string contentHtml = FindHtml(inputDir, "content");

Console.WriteLine($"Header  : {headerHtml}");
Console.WriteLine($"Footer  : {footerHtml}");
Console.WriteLine($"Content : {contentHtml}");
Console.WriteLine($"Page    : {FinalPageWidth:F0} x {FinalPageHeight:F0} pt ({(Landscape ? "landscape" : "portrait")})");

string headerPdf  = Path.Combine(outputDir, "_header.pdf");
string footerPdf  = Path.Combine(outputDir, "_footer.pdf");

// 1) GdPicture 14.4.1's HTML engine ignores HtmlPageWidth/HTMLViewPortWidth
//    and always renders at its default canvas width (~1432pt). To preserve
//    readable text after the final scale-down to A4, we apply a matching
//    HTMLScale that magnifies the layout so that 1 CSS px ≈ 1 final A4 pt.
//    Pipeline:
//      a. Probe-render header at scale 1.0 to discover the engine's natural
//         render width.
//      b. Compute nativeScale = nativeContentW / finalContentW (≈ 2.5).
//      c. Re-render everything with HTMLScale = nativeScale, so content
//         renders ~2.5x larger horizontally too — but the engine still emits
//         a ~1432pt-wide page; our scale-to-fit + final scale-down then
//         restores natural visible size on A4.
float finalContentWidth = FinalPageWidth - 2f * SidePadding;

string probePdf = Path.Combine(outputDir, "_probe.pdf");
ConvertHtmlToPdf(headerHtml, probePdf, finalContentWidth);
float nativeProbeWidth;
using (GdPicturePDF probe = new GdPicturePDF())
{
    Check(probe.LoadFromFile(probePdf, false), "probe load");
    Check(probe.SelectPage(1), "probe select");
    nativeProbeWidth = probe.GetPageWidth();
    Console.WriteLine($"Probe page size: {nativeProbeWidth:F1} x {probe.GetPageHeight():F1}");
    probe.CloseDocument();
}
File.Delete(probePdf);

float nativeContentW = nativeProbeWidth;
float nativeScale = nativeContentW / finalContentWidth;
// CSS zoom factor: pick the value that makes the logical layout viewport
// equal a "browser-equivalent" width of `finalContentW * (96/72)` px. With
// the engine viewport pinned at `nativeContentW` pt, zoom = nativeContentW /
// (finalContentW * 96/72) = nativeScale * 72/96. This keeps the layout
// roomy enough to avoid header/cell wrapping while still magnifying the
// content so it is readable after the final scale-down to A4.
const float HtmlRenderScale = 96f / 72f;
float zoomFactor = nativeScale / HtmlRenderScale;
Console.WriteLine($"Probed native render width = {nativeContentW:F1} pt, nativeScale = {nativeScale:F3}, zoom = {zoomFactor:F3}");

ConvertHtmlToPdf(headerHtml, headerPdf, finalContentWidth, zoomFactor);
ConvertHtmlToPdf(footerHtml, footerPdf, finalContentWidth, zoomFactor);

GdPicturePDF headerSrc = new GdPicturePDF();
GdPicturePDF footerSrc = new GdPicturePDF();
Check(headerSrc.LoadFromFile(headerPdf, false), "load header");
Check(footerSrc.LoadFromFile(footerPdf, false), "load footer");
float PageWidth  = FinalPageWidth  * nativeScale;
float PageHeight = FinalPageHeight * nativeScale;
float WorkingSidePadding = SidePadding * nativeScale;
float WorkingTopPadding = TopPadding * nativeScale;
float WorkingBottomPadding = BottomPadding * nativeScale;
float WorkingGapHeaderToContent = GapHeaderToContent * nativeScale;
float WorkingGapContentToFooter = GapContentToFooter * nativeScale;
Console.WriteLine($"Native scale = {nativeScale:F3} (working page = {PageWidth:F0} x {PageHeight:F0} pt)");

// Normalize header & footer to exactly the working content width.
ScaleToWidth(ref headerSrc, nativeContentW);
ScaleToWidth(ref footerSrc, nativeContentW);

Check(headerSrc.SelectPage(1), "select header for height");
float headerH = headerSrc.GetPageHeight();
Check(footerSrc.SelectPage(1), "select footer for height");
float footerH = footerSrc.GetPageHeight();

float innerH = PageHeight - WorkingTopPadding - headerH - footerH - WorkingBottomPadding;
if (innerH <= WorkingGapHeaderToContent + WorkingGapContentToFooter)
{
    throw new InvalidOperationException(
        $"Header ({headerH:F1}) + Footer ({footerH:F1}) + paddings/gaps leave no space.");
}
float maxChunkBand = innerH - WorkingGapHeaderToContent - WorkingGapContentToFooter;

Console.WriteLine($"Heights: header={headerH:F1} pt, footer={footerH:F1} pt (working coords)");
Console.WriteLine($"Max content band per page = {maxChunkBand:F1} pt (working)");

// 3) Split content HTML by table rows into chunks that fit within `maxChunkBand`
//    in working/native coords (no DPI rescale: the engine already renders at
//    that width).
List<string> chunkHtmlPaths = SplitContentIntoChunks(contentHtml, finalContentWidth, maxChunkBand, tempDir, zoomFactor);
Console.WriteLine($"Content split into {chunkHtmlPaths.Count} chunk(s).");

// 4) Render each chunk HTML and normalize its width to nativeContentW.
List<GdPicturePDF> chunkSrcs = new List<GdPicturePDF>();
foreach (string ch in chunkHtmlPaths)
{
    string pdfPath = Path.ChangeExtension(ch, ".pdf");
    ConvertHtmlToPdf(ch, pdfPath, finalContentWidth, zoomFactor);
    GdPicturePDF chunkPdf = new GdPicturePDF();
    Check(chunkPdf.LoadFromFile(pdfPath, false), $"load chunk {Path.GetFileName(pdfPath)}");
    ScaleToWidth(ref chunkPdf, nativeContentW);
    chunkSrcs.Add(chunkPdf);
}

// 5) Merge header + footer + all chunks into one working document.
GdPicturePDF[] sources = new GdPicturePDF[2 + chunkSrcs.Count];
sources[0] = headerSrc;
sources[1] = footerSrc;
for (int i = 0; i < chunkSrcs.Count; i++) sources[2 + i] = chunkSrcs[i];

using GdPicturePDF doc = headerSrc.MergeDocuments(sources);
Check(headerSrc.GetStat(), "merge documents");
GdPictureStatus s;

const int HeaderPageNum = 1;
const int FooterPageNum = 2;
int FirstChunkPageNum = 3;

// 6) Set permanent header MediaBox (TopPadding above + GapHeaderToContent below).
s = doc.SelectPage(HeaderPageNum); Check(s, "select header");
s = doc.SetPageBox(PdfPageBox.PdfPageBoxMediaBox,
    Left: 0f, Top: headerH + WorkingTopPadding, Right: PageWidth, Bottom: -WorkingGapHeaderToContent);
Check(s, "set header MediaBox");

// 7) Compose each final page = [headerDup, chunkDup, footerDup] stacked.
//    Footer's MediaBox top is extended per-page so that
//      (TopPadding + headerH + GapHeaderToContent) + chunkH + footerExt + footerH + BottomPadding = PageHeight
for (int i = 0; i < chunkSrcs.Count; i++)
{
    int chunkPage = FirstChunkPageNum + i;
    s = doc.SelectPage(chunkPage); Check(s, $"select chunk page {chunkPage}");
    float chunkH = doc.GetPageHeight();
    float footerExtension = innerH - WorkingGapHeaderToContent - chunkH;
    if (footerExtension < 0f) footerExtension = 0f;
    float footerBoxHeight = footerH + footerExtension;

    int before;

    before = doc.GetPageCount();
    s = doc.SelectPage(HeaderPageNum); Check(s, "select header");
    s = doc.DuplicatePage(1);          Check(s, "duplicate header");
    int headerDup = before + 1;

    before = doc.GetPageCount();
    s = doc.SelectPage(chunkPage); Check(s, $"select chunk {chunkPage}");
    s = doc.DuplicatePage(1);      Check(s, "duplicate chunk");
    int chunkDup = before + 1;

    before = doc.GetPageCount();
    s = doc.SelectPage(FooterPageNum); Check(s, "select footer");
    s = doc.DuplicatePage(1);          Check(s, "duplicate footer");
    int footerDup = before + 1;

    s = doc.SelectPage(footerDup); Check(s, "select footer dup");
    s = doc.SetPageBox(PdfPageBox.PdfPageBoxMediaBox,
        Left: 0f, Top: footerBoxHeight, Right: PageWidth, Bottom: -WorkingBottomPadding);
    Check(s, "set footer dup MediaBox");

    int[] block = { headerDup, chunkDup, footerDup };
    s = doc.MergePages(block, true);
    Check(s, $"merge block {i + 1}/{chunkSrcs.Count}");
}

// 8) Remove the initial 2 + chunkCount source pages.
int initialPagesToRemove = 2 + chunkSrcs.Count;
for (int p = initialPagesToRemove; p >= 1; p--)
{
    s = doc.DeletePage(p); Check(s, $"delete page {p}");
}

Console.WriteLine($"Final page count: {doc.GetPageCount()}");

// 9) Normalize each composed page onto a fresh A4 page, scaling down from
//    working/native coords by `1/nativeScale` so text lands at readable size.
float finalScale = 1f / nativeScale;
using GdPicturePDF normalized = new GdPicturePDF();
Check(normalized.NewPDF(), "new normalized pdf");
int pageCount = doc.GetPageCount();
for (int p = 1; p <= pageCount; p++)
{
    Check(doc.SelectPage(p), $"select source page {p}");
    Check(normalized.NewPage(FinalPageWidth, FinalPageHeight), $"normalized new page {p}");
    Check(normalized.OverlayPage(doc, p, SidePadding, 0f, finalScale, finalScale), $"overlay normalized page {p}");
}

while (normalized.GetPageCount() > pageCount)
{
    Check(normalized.DeletePage(1), "trim placeholder page");
}

s = normalized.SaveToFile(finalPdf);
Check(s, "save final");
normalized.CloseDocument();
doc.CloseDocument();

headerSrc.Dispose();
footerSrc.Dispose();
foreach (var c in chunkSrcs) c.Dispose();
File.Delete(headerPdf);
File.Delete(footerPdf);
if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);

Console.WriteLine($"Done -> {finalPdf}");
Console.WriteLine($"Size  : {new FileInfo(finalPdf).Length / 1024.0:F1} KB");

static string FindHtml(string dir, params string[] needles)
{
    foreach (string f in Directory.GetFiles(dir, "*.html", SearchOption.TopDirectoryOnly))
    {
        string lower = Path.GetFileName(f).ToLowerInvariant();
        foreach (string n in needles)
        {
            if (lower.Contains(n)) return f;
        }
    }
    throw new FileNotFoundException($"No HTML matching [{string.Join(',', needles)}] in {dir}");
}

// Default font fallback applied to every conversion. Generic, document-agnostic:
// if the source HTML declares its own `font-family`, this rule does not override
// it (no `!important`); it only kicks in for documents that rely on the generic
// `sans-serif`/`serif` resolution from the headless engine.
const string DefaultFontFallbackCss =
    "html, body { font-family: -apple-system, BlinkMacSystemFont, \"Segoe UI\", " +
    "\"Helvetica Neue\", Helvetica, Arial, \"Liberation Sans\", \"Noto Sans\", sans-serif; }";

// One-style-block patch. Both the pre-layout `zoom` (needed because the
// converter ignores `HtmlPageWidth`) and the font-family fallback must run
// before layout, so they are injected into the source document's <head>.
// Note: setting `HTMLCustomCSS` instead would arrive too late and triggers a
// rendering path that pads pages to ~752pt regardless of content.
// The fallback uses no `!important`, so any document declaring its own
// font-family wins.
static void ConvertHtmlToPdf(string htmlPath, string pdfPath, float pageWidthPt, float zoomFactor = 1f)
{
    string z = zoomFactor.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
    string injected = $"<style>{DefaultFontFallbackCss} html {{ zoom: {z}; }}</style>";
    string html = File.ReadAllText(htmlPath);
    int headIdx = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
    if (headIdx >= 0)
    {
        int closeIdx = html.IndexOf('>', headIdx);
        html = closeIdx >= 0 ? html.Insert(closeIdx + 1, injected) : injected + html;
    }
    else
    {
        html = injected + html;
    }
    string patchedPath = Path.ChangeExtension(pdfPath, ".patched.html");
    File.WriteAllText(patchedPath, html);

    using GdPictureDocumentConverter conv = new GdPictureDocumentConverter();
    conv.HtmlPageWidth = pageWidthPt;
    conv.HtmlPageHeight = 1f;
    conv.HtmlPageMarginTop = 0f;
    conv.HtmlPageMarginBottom = 0f;
    conv.HtmlPageMarginLeft = 0f;
    conv.HtmlPageMarginRight = 0f;
    conv.HtmlEmulationType = EmulationMedia.Print;

    // The HTML engine occasionally fails with UnknownImageFormat on a valid page:
    // retry a few times before giving up.
    GdPictureStatus s = GdPictureStatus.GenericError;
    for (int attempt = 1; attempt <= 3; attempt++)
    {
        using (FileStream input = File.OpenRead(patchedPath))
        using (FileStream output = File.Create(pdfPath))
        {
            s = conv.ConvertToPDF(input, GdPicture14.DocumentFormat.DocumentFormatHTML,
                output, PdfConformance.PDF1_5);
        }
        if (s == GdPictureStatus.OK) break;
        Console.WriteLine($"  Conversion of {Path.GetFileName(htmlPath)} returned {s}, attempt {attempt} of 3.");
        Thread.Sleep(500);
    }
    Check(s, $"convert html -> pdf ({Path.GetFileName(htmlPath)})");
    File.Delete(patchedPath);
}

static void ScaleViaOverlay(ref GdPicturePDF pdf, float scale)
{
    Check(pdf.SelectPage(1), "select original page for overlay");
    float origW = pdf.GetPageWidth();
    float origH = pdf.GetPageHeight();

    GdPicturePDF dst = new GdPicturePDF();
    Check(dst.NewPDF(), "new pdf");
    Check(dst.NewPage(origW * scale, origH * scale), "new page for overlay");
    Check(dst.OverlayPage(pdf, 1, 0f, 0f, scale, scale), "overlay page with scale");

    pdf.Dispose();
    pdf = dst;
}

// Scales `pdf` so its page width matches `targetWidthPt`, regardless of the
// HTML engine's actual chosen render width. Height scales by the same factor
// to preserve aspect ratio.
static void ScaleToWidth(ref GdPicturePDF pdf, float targetWidthPt)
{
    Check(pdf.SelectPage(1), "select page for width-fit scale");
    float origW = pdf.GetPageWidth();
    if (origW <= 0f) throw new InvalidOperationException("page width is zero");
    float scale = targetWidthPt / origW;
    ScaleViaOverlay(ref pdf, scale);
}

// Splits the content HTML by rows of its largest table into chunks whose
// rendered height (at `renderWidthPt` width) does not exceed `maxChunkHeightPt`.
// The header row(s) (any leading <tr>s containing <th>) are repeated on each
// chunk. Returns paths to the per-chunk HTML files written into `tempDir`.
//
// Strategy:
//  1. Locate the table with the most <tr>s (innermost data table in nested layouts).
//  2. Detect leading "header" rows = leading <tr>s containing only <th>.
//  3. Render the bare table (header rows only, no data) to measure baseline height.
//  4. Render each data row individually (with the header) to measure cumulative
//     height; subtract the header to get the row's incremental contribution.
//  5. Greedy-pack data rows into chunks until adding the next row would exceed
//     the budget; close the chunk and start the next.
//  6. Emit one HTML per chunk, preserving the original document structure
//     (head, styles, surrounding tables) with only that chunk's data rows.
static List<string> SplitContentIntoChunks(
    string contentHtmlPath,
    float renderWidthPt,
    float maxChunkHeightPt,
    string tempDir,
    float zoomFactor)
{
    HtmlDocument doc = new HtmlDocument();
    doc.Load(contentHtmlPath);

    // Find the table with the most direct/tbody <tr>s.
    HtmlNodeCollection? allTables = doc.DocumentNode.SelectNodes("//table");
    if (allTables == null)
        return new List<string> { contentHtmlPath };

    HtmlNode? splitTable = null;
    int splitTableRowCount = 0;
    foreach (HtmlNode t in allTables)
    {
        int rc = (t.SelectNodes("./tr") ?? new HtmlNodeCollection(null)).Count
               + (t.SelectNodes("./tbody/tr") ?? new HtmlNodeCollection(null)).Count;
        if (rc > splitTableRowCount)
        {
            splitTable = t;
            splitTableRowCount = rc;
        }
    }

    if (splitTable == null || splitTableRowCount < 2)
        return new List<string> { contentHtmlPath };

    // Locate the row container (tbody if present, else the table itself).
    HtmlNode rowParent = splitTable.SelectSingleNode("./tbody") ?? splitTable;
    List<HtmlNode> allRows = (rowParent.SelectNodes("./tr") ?? new HtmlNodeCollection(null)).ToList();
    if (allRows.Count == 0)
        return new List<string> { contentHtmlPath };

    // Header rows: leading <tr>s that contain only <th> (no <td>).
    int headerRowCount = 0;
    while (headerRowCount < allRows.Count)
    {
        HtmlNode tr = allRows[headerRowCount];
        bool hasTh = tr.SelectNodes(".//th") != null;
        bool hasTd = tr.SelectNodes(".//td") != null;
        if (hasTh && !hasTd) headerRowCount++;
        else break;
    }

    List<HtmlNode> headerRows = allRows.Take(headerRowCount).ToList();
    List<HtmlNode> dataRows   = allRows.Skip(headerRowCount).ToList();

    if (dataRows.Count == 0)
        return new List<string> { contentHtmlPath };

    // Detach all rows from rowParent so we can re-attach selectively per chunk.
    foreach (HtmlNode r in allRows) r.Remove();

    // Helper: write a chunk HTML containing headerRows + given data rows.
    string WriteChunkHtml(IEnumerable<HtmlNode> chunkData, string suffix)
    {
        foreach (HtmlNode hr in headerRows) rowParent.AppendChild(hr);
        List<HtmlNode> attached = new List<HtmlNode>();
        foreach (HtmlNode dr in chunkData)
        {
            rowParent.AppendChild(dr);
            attached.Add(dr);
        }
        string p = Path.Combine(tempDir, $"_chunk_{suffix}.html");
        doc.Save(p);
        foreach (HtmlNode n in attached) n.Remove();
        foreach (HtmlNode hr in headerRows) hr.Remove();
        return p;
    }

    // Two reference renders: baseline (header rows only) + full (all data rows).
    // From these we derive an average row height used for greedy packing.
    Console.WriteLine("  Measuring baseline (header only)...");
    string baselinePath = WriteChunkHtml(Array.Empty<HtmlNode>(), "baseline");
    float baselineH = MeasureHtmlPdfHeight(baselinePath, renderWidthPt, zoomFactor);
    File.Delete(baselinePath);

    Console.WriteLine($"  Measuring full content ({dataRows.Count} rows)...");
    string fullPath = WriteChunkHtml(dataRows, "full");
    float fullH = MeasureHtmlPdfHeight(fullPath, renderWidthPt, zoomFactor);
    File.Delete(fullPath);

    float avgRowH = Math.Max((fullH - baselineH) / dataRows.Count, 1f);
    int approxRowsPerChunk = Math.Max(1, (int)Math.Floor((maxChunkHeightPt - baselineH) / avgRowH));
    Console.WriteLine($"  Baseline={baselineH:F1} pt, full={fullH:F1} pt, avg row={avgRowH:F1} pt, ~{approxRowsPerChunk} rows/chunk");

    // Greedy pack with the average estimate, then verify each chunk by rendering
    // it. If the chunk overflows the budget, peel rows off the end and re-render
    // until it fits.
    List<string> chunkPaths = new List<string>();
    int idx = 0;
    int chunkIdx = 0;
    while (idx < dataRows.Count)
    {
        int take = Math.Min(approxRowsPerChunk, dataRows.Count - idx);
        while (true)
        {
            List<HtmlNode> chunk = dataRows.Skip(idx).Take(take).ToList();
            string p = WriteChunkHtml(chunk, chunkIdx.ToString());
            float h = MeasureHtmlPdfHeight(p, renderWidthPt, zoomFactor);
            if (h <= maxChunkHeightPt || take == 1)
            {
                Console.WriteLine($"  Chunk {chunkIdx}: {take} rows, height {h:F1} pt");
                chunkPaths.Add(p);
                idx += take;
                break;
            }
            File.Delete(p);
            take--;
        }
        chunkIdx++;
    }
    return chunkPaths;
}

static float MeasureHtmlPdfHeight(string htmlPath, float pageWidthPt, float zoomFactor = 1f)
{
    string tmpPdf = Path.ChangeExtension(htmlPath, ".pdf")!;
    ConvertHtmlToPdf(htmlPath, tmpPdf, pageWidthPt, zoomFactor);
    using GdPicturePDF pdf = new GdPicturePDF();
    Check(pdf.LoadFromFile(tmpPdf, false), $"load measure pdf {Path.GetFileName(tmpPdf)}");
    Check(pdf.SelectPage(1), "select measure page");
    float h = pdf.GetPageHeight();
    pdf.CloseDocument();
    File.Delete(tmpPdf);
    return h;
}

static void Check(GdPictureStatus s, string what)
{
    if (s != GdPictureStatus.OK)
    {
        throw new InvalidOperationException($"{what} failed: {s}");
    }
}
