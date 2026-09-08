using System.Collections.Generic;
using DocDr.App.Services;
using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

/// <summary>Records what a view-model asked to be rendered / dropped, without touching PDFium.</summary>
internal sealed class FakeRenderQueue : IRenderQueue
{
    public List<RenderRequest> Enqueued { get; } = [];
    public int ClearCount { get; private set; }

    public void Enqueue(RenderRequest request) => Enqueued.Add(request);

    public void Clear() => ClearCount++;
}

/// <summary>Captures the last edit / delete the format toolbar drove, and hands back a fixed
/// "custom" colour.</summary>
internal sealed class FakeFormatHost : IAnnotationFormatHost
{
    public uint CustomColor { get; set; } = 0xFF123456;
    public uint? CustomColorRequestedWith { get; private set; }
    public Guid? LastResizeId { get; private set; }
    public double LastResizeFontSize { get; private set; }
    public Guid? DeletedId { get; private set; }

    private Func<PdfAnnotation, PdfAnnotation>? _lastMutate;
    public Guid? LastMutateId { get; private set; }

    public void ApplyFormat(Guid id, Func<PdfAnnotation, PdfAnnotation> mutate)
    {
        LastMutateId = id;
        _lastMutate = mutate;
    }

    /// <summary>Run the last recorded edit against <paramref name="original"/>.</summary>
    public PdfAnnotation Apply(PdfAnnotation original) =>
        _lastMutate is null ? original : _lastMutate(original);

    public uint? PickCustomColor(uint fallback)
    {
        CustomColorRequestedWith = fallback;
        return CustomColor;
    }

    public void ResizeTextBoxForFont(Guid id, double fontSize)
    {
        LastResizeId = id;
        LastResizeFontSize = fontSize;
    }

    public void DeleteAnnotation(Guid id) => DeletedId = id;
}
