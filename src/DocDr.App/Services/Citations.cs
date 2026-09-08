using System.Linq;
using System.Windows;

namespace DocDr.App.Services;

/// <summary>Builds a plain-text citation for a clause or a selected passage and puts it on the clipboard.</summary>
public static class Citations
{
    /// <summary>
    /// "<i>quote</i>" — <c>Source, cl. 6.2.5, p. 88</c>. <paramref name="clauseNumber"/> and
    /// <paramref name="quote"/> are optional; a bare source + page is still a valid citation.
    /// </summary>
    public static string Format(string source, string? clauseNumber, string pageLabel, string? quote)
    {
        string reference = string.IsNullOrEmpty(clauseNumber)
            ? $"{source}, p. {pageLabel}"
            : $"{source}, cl. {clauseNumber}, p. {pageLabel}";

        return string.IsNullOrWhiteSpace(quote)
            ? reference
            : $"“{quote.Trim()}”\n— {reference}";
    }

    /// <summary><c>Source, cl. 6.5 (Concrete cover), p. 88</c>.</summary>
    public static string ForClause(string source, string number, string? title, string pageLabel)
    {
        string head = number.Length is 1 or 2 && number.All(char.IsAsciiLetterUpper)
            ? $"{source}, Annex {number}"
            : $"{source}, cl. {number}";
        string named = string.IsNullOrWhiteSpace(title) ? head : $"{head} ({title.Trim()})";
        return $"{named}, p. {pageLabel}";
    }

    public static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard was locked by another process; nothing useful to do.
        }
    }
}
