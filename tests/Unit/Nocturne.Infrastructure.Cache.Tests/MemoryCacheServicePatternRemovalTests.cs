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
    public async Task A_key_written_twice_concurrently_stays_tracked_whichever_writer_tracks_it_last()
    {
        // A commits value 1; before A tracks the key, B replaces it with value 2 and tracks it; then
        // A tracks it, and only then does value 1's eviction run.
        var ct = TestContext.Current.CancellationToken;
        const string key = "tenant-a:full:live:entries:current";
        _memory.AfterNextCommit = () =>
            _cache.SetAsync(key, "second", TimeSpan.FromMinutes(5), ct).GetAwaiter().GetResult();
        await _cache.SetAsync(key, "first", TimeSpan.FromMinutes(5), ct);

        _memory.RunPendingEvictions();
        await _cache.RemoveByPatternAsync("tenant-a:*", ct);

        (await _cache.GetAsync<string>(key, ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_key_refilled_during_a_pattern_removal_stays_tracked()
    {
        var ct = TestContext.Current.CancellationToken;
        const string key = "tenant-a:full:live:entries:current";
        await _cache.SetAsync(key, "stale", TimeSpan.FromMinutes(5), ct);
        _memory.AfterNextRemove = () =>
            _cache.SetAsync(key, "refilled", TimeSpan.FromMinutes(5), ct).GetAwaiter().GetResult();

        await _cache.RemoveByPatternAsync("tenant-a:*", ct);
        _memory.RunPendingEvictions();
        (await _cache.GetAsync<string>(key, ct)).Should().Be("refilled");
        await _cache.RemoveByPatternAsync("tenant-a:*", ct);

        (await _cache.GetAsync<string>(key, ct)).Should().BeNull();
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

        /// <summary>Runs once, inside the next commit, after the entry is stored.</summary>
        public Action? AfterNextCommit { get; set; }

        /// <summary>Runs once, inside the next removal, after the entry is gone.</summary>
        public Action? AfterNextRemove { get; set; }

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
            Once(() => AfterNextRemove, () => AfterNextRemove = null);
        }

        public void Dispose()
        {
        }

        private void Commit(Entry entry)
        {
            if (_entries.Remove(entry.Key, out var replaced))
                Defer(replaced, EvictionReason.Replaced);
            _entries[entry.Key] = entry;
            Once(() => AfterNextCommit, () => AfterNextCommit = null);
        }

        private static void Once(Func<Action?> read, Action clear)
        {
            if (read() is not { } hook)
                return;
            clear();
            hook();
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
