using System.Text;

namespace DocDr.Pdf;

/// <summary>
/// Word wrapping and text measurement for the stamp-backed text box / callout, using the
/// standard Helvetica advance-width table so the on-screen overlay, the saved appearance and
/// the auto-fit box size all agree.
/// </summary>
public static class PdfTextWrap
{
    /// <summary>Baseline-to-baseline spacing as a multiple of the font size.</summary>
    public const double LineHeightFactor = 1.25;

    /// <summary>Padding between the text and the box edge, in points.</summary>
    public const double Inset = 3.0;

    // Helvetica (AFM) advance widths in 1/1000 em, for ASCII 32..126.
    private static readonly ushort[] Widths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static double CharWidth(char c, double fontSize)
    {
        int w = c is >= ' ' and <= '~' ? Widths[c - ' '] : 556;
        return w / 1000.0 * fontSize;
    }

    private static double LineWidth(string line, double fontSize)
    {
        double w = 0;
        foreach (char c in line)
        {
            w += CharWidth(c, fontSize);
        }

        return w;
    }

    /// <summary>Greedy word wrap of <paramref name="text"/> into lines no wider than
    /// <paramref name="maxWidth"/> points (honouring existing line breaks).</summary>
    public static IReadOnlyList<string> Wrap(string text, double maxWidth, double fontSize)
    {
        var lines = new List<string>();
        maxWidth = Math.Max(CharWidth('W', fontSize), maxWidth);

        foreach (string paragraph in (text ?? string.Empty).ReplaceLineEndings("\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = new StringBuilder();
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length == 0 || LineWidth(candidate, fontSize) <= maxWidth)
                {
                    current.Clear().Append(candidate);
                }
                else
                {
                    lines.Add(current.ToString());
                    current.Clear().Append(word);
                }

                // A single word longer than the line: hard-break it.
                while (LineWidth(current.ToString(), fontSize) > maxWidth && current.Length > 1)
                {
                    int cut = current.Length;
                    while (cut > 1 && LineWidth(current.ToString(0, cut), fontSize) > maxWidth)
                    {
                        cut--;
                    }

                    lines.Add(current.ToString(0, cut));
                    current.Remove(0, cut);
                }
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }
        }

        return lines.Count > 0 ? lines : [string.Empty];
    }

    /// <summary>
    /// The box size (points) that just fits <paramref name="text"/> — wrapped to at most
    /// <paramref name="maxWidth"/> — including the <see cref="Inset"/> on every edge.
    /// </summary>
    public static (double Width, double Height) FittedSize(string? text, double maxWidth, double fontSize)
    {
        if (fontSize <= 0)
        {
            fontSize = PdfAnnotation.DefaultFontSize;
        }

        // The on-screen overlay renders with the system UI font, not the AFM Helvetica this
        // table models, so leave a little slack on both axes to avoid clipping the last word / line.
        const double widthSlack = 1.06;
        const double heightSlack = 0.4; // extra fraction of a line

        IReadOnlyList<string> lines = Wrap(text ?? string.Empty, maxWidth - (2 * Inset), fontSize);
        double widest = 0;
        foreach (string line in lines)
        {
            widest = Math.Max(widest, LineWidth(line, fontSize));
        }

        double width = Math.Min(maxWidth, (widest * widthSlack) + (2 * Inset));
        double height = ((lines.Count + heightSlack) * fontSize * LineHeightFactor) + (2 * Inset);
        return (width, height);
    }
}
