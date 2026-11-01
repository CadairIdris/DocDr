using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DocDr.Pdf;

namespace DocDr.App.Views;

/// <summary>
/// <c>local:Redline.Segments="{Binding Segments}"</c> on a <see cref="TextBlock"/> fills it with the
/// redline: deleted words struck through in red, inserted words underlined in blue, unchanged words
/// plain (or muted, with <c>Redline.MuteEqual</c> — context around a change).
/// </summary>
public static class Redline
{
    private static readonly Brush DeletedText = Frozen(Color.FromRgb(0xD9, 0x3A, 0x32));
    private static readonly Brush DeletedBack = Frozen(Color.FromArgb(0x30, 0xDC, 0x28, 0x28));
    private static readonly Brush InsertedText = Frozen(Color.FromRgb(0x2F, 0x7B, 0xE8));
    private static readonly Brush InsertedBack = Frozen(Color.FromArgb(0x30, 0x14, 0x6E, 0xE6));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<RedlineSegment>), typeof(Redline), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty MuteEqualProperty = DependencyProperty.RegisterAttached(
        "MuteEqual", typeof(bool), typeof(Redline), new PropertyMetadata(false, OnChanged));

    public static IReadOnlyList<RedlineSegment>? GetSegments(DependencyObject o) => (IReadOnlyList<RedlineSegment>?)o.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject o, IReadOnlyList<RedlineSegment>? value) => o.SetValue(SegmentsProperty, value);

    public static bool GetMuteEqual(DependencyObject o) => (bool)o.GetValue(MuteEqualProperty);

    public static void SetMuteEqual(DependencyObject o, bool value) => o.SetValue(MuteEqualProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        if (GetSegments(block) is not { } segments)
        {
            return;
        }

        bool mute = GetMuteEqual(block);
        for (int i = 0; i < segments.Count; i++)
        {
            RedlineSegment s = segments[i];
            if (i > 0)
            {
                block.Inlines.Add(new Run(" "));
            }

            var run = new Run(s.Text);
            switch (s.Kind)
            {
                case RedlineKind.Deleted:
                    run.Foreground = DeletedText;
                    run.Background = DeletedBack;
                    run.TextDecorations = TextDecorations.Strikethrough;
                    break;
                case RedlineKind.Inserted:
                    run.Foreground = InsertedText;
                    run.Background = InsertedBack;
                    run.TextDecorations = TextDecorations.Underline;
                    break;
                default:
                    if (mute)
                    {
                        run.SetResourceReference(TextElement.ForegroundProperty, "SecondaryTextBrush");
                    }

                    break;
            }

            block.Inlines.Add(run);
        }
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
