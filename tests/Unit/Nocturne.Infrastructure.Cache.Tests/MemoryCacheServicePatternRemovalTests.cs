using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Cache.Services;
using Xunit;

namespace Nocturne.Infrastructure.Cache.Tests;

[Trait("Category", "Cache")]
public class MemoryCacheServicePatternRemovalTests
{
    private readonly DeferredEvictionCache _memory = new();
    private readonly MemoryCacheService _cache;

    public MemoryCacheServicePatternRemovalTests()
    {
        _cache = new MemoryCacheService(
            _memory, Options.Create(new CacheConfiguration()), NullLogger<MemoryCacheService>.Instance);
    }

    [Fact]
    public async Task An_overwritten_key_is_still_removed_by_pattern_when_the_replaced_entry_is_evicted_late()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cache.SetAsync("tenant-a:full:live:entries:current", "first", TimeSpan.FromMinutes(5), ct);
        await _cache.SetAsync("tenant-a:full:live:entries:current", "second", TimeSpan.FromMinutes(5), ct);

        _memory.RunPendingEvictions();
        await _cache.RemoveByPatternAsync("tenant-a:*", ct);

        (await _cache.GetAsync<string>("tenant-a:full:live:entries:current", ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_pattern_with_a_literal_first_segment_leaves_other_segments_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cache.SetAsync("tenant-a:full:live:entries:current", "a", TimeSpan.FromMinutes(5), ct);
        await _cache.SetAsync("tenant-b:full:live:entries:current", "b", TimeSpan.FromMinutes(5), ct);

        await _cache.RemoveByPatternAsync("tenant-a:*:*:entries:*", ct);

        (await _cache.GetAsync<string>("tenant-a:full:live:entries:current", ct)).Should().BeNull();
        (await _cache.GetAsync<string>("tenant-b:full:live:entries:current", ct)).Should().Be("b");
    }

    [Fact]
    public async Task A_pattern_with_a_wildcard_first_segment_reaches_every_segment()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cache.SetAsync("tenant-a:full:live:entries:current", "a", TimeSpan.FromMinutes(5), ct);
        await _cache.SetAsync("tenant-b:full:live:entries:current", "b", TimeSpan.FromMinutes(5), ct);

        await _cache.RemoveByPatternAsync("*:entries:current", ct);

        (await _cache.GetAsync<string>("tenant-a:full:live:entries:current", ct)).Should().BeNull();
        (await _cache.GetAsync<string>("tenant-b:full:live:entries:current", ct)).Should().BeNull();
    }

    /// <summary>
    /// An <see cref="IMemoryCache"/> that holds every post-eviction callback until the test runs it,
    /// standing in for <see cref="MemoryCache"/>, which runs them on the thread pool.
    /// </summary>
    private sealed class DeferredEvictionCache : IMemoryCache
    {
        private readonly Dictionary<object, Entry> _entries = [];
        private readonly List<Action> _pending = [];

        public void RunPendingEvictions()
        {
            foreach (var eviction in _pending.ToList())
                eviction();
            _pending.Clear();
        }

        public ICacheEntry CreateEntry(object key) => new Entry(key, this);

        public bool TryGetValue(object key, out object? value)
        {
            var found = _entries.TryGetValue(key, out var entry);
            value = entry?.Value;
            return found;
        }

        public void Remove(object key)
        {
            if (_entries.Remove(key, out var entry))
                Defer(entry, EvictionReason.Removed);
        }

        public void Dispose()
        {
        }

        private void Commit(Entry entry)
        {
            if (_entries.Remove(entry.Key, out var replaced))
                Defer(replaced, EvictionReason.Replaced);
            _entries[entry.Key] = entry;
        }

        private void Defer(Entry entry, EvictionReason reason) =>
            _pending.Add(() =>
            {
                foreach (var registration in entry.PostEvictionCallbacks)
                    registration.EvictionCallback!(entry.Key, entry.Value, reason, registration.State);
            });

        private sealed class Entry(object key, DeferredEvictionCache owner) : ICacheEntry
        {
            public object Key { get; } = key;
            public object? Value { get; set; }
            public DateTimeOffset? AbsoluteExpiration { get; set; }
            public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }
            public TimeSpan? SlidingExpiration { get; set; }
            public IList<IChangeToken> ExpirationTokens { get; } = [];
            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; } = [];
            public CacheItemPriority Priority { get; set; }
            public long? Size { get; set; }

            public void Dispose() => owner.Commit(this);
        }
    }
}
