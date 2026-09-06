using System.Globalization;

namespace DocDr.Pdf;

/// <summary>
/// PDF date strings (<c>D:YYYYMMDDHHmmSSOHH'mm'</c>, PDF 1.7 §7.9.4). Only the pieces present
/// are parsed; the rest default. Formatting always produces a fully specified UTC-offset value.
/// </summary>
public static class PdfDate
{
    public static string Now() => Format(DateTimeOffset.Now);

    public static string Format(DateTimeOffset value)
    {
        TimeSpan offset = value.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        return $"D:{value:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):D2}'{Math.Abs(offset.Minutes):D2}'";
    }

    public static bool TryParse(string? pdfDate, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(pdfDate))
        {
            return false;
        }

        string s = pdfDate.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        if (s.Length < 4 || !int.TryParse(s[..4], out int year))
        {
            return false;
        }

        int Part(int start, int len, int fallback) =>
            s.Length >= start + len && int.TryParse(s.Substring(start, len), out int p) ? p : fallback;

        int month = Part(4, 2, 1);
        int day = Part(6, 2, 1);
        int hour = Part(8, 2, 0);
        int minute = Part(10, 2, 0);
        int second = Part(12, 2, 0);

        TimeSpan offset = TimeSpan.Zero;
        int oi = 14;
        if (s.Length > oi && (s[oi] is '+' or '-' or 'Z'))
        {
            if (s[oi] != 'Z')
            {
                int oh = Part(oi + 1, 2, 0);
                int om = s.Length >= oi + 6 && int.TryParse(s.AsSpan(oi + 4, 2), out int m) ? m : 0;
                offset = new TimeSpan(oh, om, 0);
                if (s[oi] == '-')
                {
                    offset = -offset;
                }
            }
        }

        try
        {
            month = Math.Clamp(month, 1, 12);
            int maxDay = DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month);
            value = new DateTimeOffset(
                Math.Clamp(year, 1, 9999), month, Math.Clamp(day, 1, maxDay),
                Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), Math.Clamp(second, 0, 59), offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>A friendly "yyyy-MM-dd HH:mm" for display, or the raw string if it doesn't parse.</summary>
    public static string ForDisplay(string? pdfDate) =>
        TryParse(pdfDate, out DateTimeOffset value)
            ? value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : pdfDate?.Trim() ?? string.Empty;
}
