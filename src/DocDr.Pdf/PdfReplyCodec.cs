using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocDr.Pdf;

/// <summary>
/// Serialises a comment's <see cref="PdfAnnotation.Replies"/> to and from the compact JSON string
/// DocDr stashes in the parent annotation's private <c>/DocDrThread</c> key.
/// <para>
/// PDFium has no setter for an indirect-reference dictionary entry, so DocDr can't emit a
/// standard <c>/IRT</c> reply-annotation chain. Keeping the whole thread on the parent as one
/// string round-trips losslessly through PDFium (and through any reader that preserves unknown
/// annotation keys); other readers still show the opening comment.
/// </para>
/// </summary>
internal static class PdfReplyCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Entry(string Id, string T, string? A, string C, string M);

    public static string Encode(IReadOnlyList<PdfReply> replies)
    {
        var entries = replies.Select(r => new Entry(
            r.Id.ToString("N"),
            r.Text,
            string.IsNullOrWhiteSpace(r.Author) ? null : r.Author,
            r.Created.ToString("o"),
            r.Modified.ToString("o"))).ToArray();

        return JsonSerializer.Serialize(entries, Options);
    }

    public static IReadOnlyList<PdfReply> Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        Entry[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Entry[]>(json, Options);
        }
        catch (JsonException)
        {
            return [];
        }

        if (entries is null)
        {
            return [];
        }

        var result = new List<PdfReply>(entries.Length);
        foreach (Entry e in entries)
        {
            if (string.IsNullOrEmpty(e.T))
            {
                continue;
            }

            Guid id = Guid.TryParse(e.Id, out Guid g) ? g : Guid.NewGuid();
            DateTimeOffset created = DateTimeOffset.TryParse(e.C, out DateTimeOffset c) ? c : DateTimeOffset.Now;
            DateTimeOffset modified = DateTimeOffset.TryParse(e.M, out DateTimeOffset m) ? m : created;
            result.Add(new PdfReply(id, e.T, e.A, created, modified));
        }

        return result;
    }
}
