namespace DocDr.Pdf.Tests;

public sealed class SizedLruCacheTests
{
    [Fact]
    public void Hit_returns_the_stored_value()
    {
        var cache = new SizedLruCache<string, object>(maxEntries: 10, maxBytes: 1000);
        object value = new();
        cache.Add("a", value, 10);

        Assert.True(cache.TryGet("a", out object? hit));
        Assert.Same(value, hit);
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void Evicts_least_recently_used_once_the_entry_cap_is_hit()
    {
        var cache = new SizedLruCache<int, string>(maxEntries: 2, maxBytes: long.MaxValue);
        cache.Add(0, "p0", 1);
        cache.Add(1, "p1", 1);
        Assert.True(cache.TryGet(0, out _)); // touch 0 — now 1 is the LRU
        cache.Add(2, "p2", 1);

        Assert.Equal(2, cache.Stats.Entries);
        Assert.True(cache.TryGet(0, out _));
        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out _));
    }

    [Fact]
    public void Evicts_by_total_byte_budget()
    {
        var cache = new SizedLruCache<int, string>(maxEntries: 100, maxBytes: 3000);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(cache.Add(i, $"p{i}", 900)); // < maxBytes/3, so each one is cacheable
        }

        Assert.Equal(3, cache.Stats.Entries); // 3600 > 3000 → oldest dropped
        Assert.True(cache.Stats.Bytes <= 3000);
        Assert.False(cache.TryGet(0, out _));
    }

    [Fact]
    public void An_entry_larger_than_a_third_of_the_budget_is_not_stored()
    {
        var cache = new SizedLruCache<int, string>(maxEntries: 100, maxBytes: 900); // third = 300
        cache.Add(0, "small", 100);

        Assert.False(cache.Add(1, "huge", 400));

        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(0, out _)); // and it didn't evict anything on its way past
    }

    [Fact]
    public void Re_adding_a_key_replaces_it_and_its_size()
    {
        var cache = new SizedLruCache<int, string>(maxEntries: 10, maxBytes: 1000);
        cache.Add(0, "old", 100);
        cache.Add(0, "new", 50);

        Assert.Equal((1, 50L), cache.Stats);
        Assert.True(cache.TryGet(0, out string? value));
        Assert.Equal("new", value);
    }

    [Fact]
    public void RemoveWhere_drops_only_matching_keys()
    {
        var cache = new SizedLruCache<(string Doc, int Page), string>(maxEntries: 10, maxBytes: 1000);
        cache.Add(("a", 0), "a0", 10);
        cache.Add(("a", 1), "a1", 10);
        cache.Add(("b", 0), "b0", 10);

        Assert.Equal(2, cache.RemoveWhere(k => k.Doc == "a"));

        Assert.Equal((1, 10L), cache.Stats);
        Assert.True(cache.TryGet(("b", 0), out _));
    }
}
