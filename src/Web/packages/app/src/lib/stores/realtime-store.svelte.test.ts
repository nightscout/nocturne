import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi, afterEach } from "vitest";
import { flushSync } from "svelte";
import Harness from "./realtime-store-harness.svelte";
import { RealtimeStore } from "./realtime-store.svelte";

describe("createRealtimeStore singleton lifecycle", () => {
  it("returns the same singleton across mounts until destroy(), then a fresh one", () => {
    let first!: RealtimeStore;
    render(Harness, { props: { onstore: (s: RealtimeStore) => (first = s) } });

    // A second mount reuses the module-level singleton.
    let cached!: RealtimeStore;
    render(Harness, { props: { onstore: (s: RealtimeStore) => (cached = s) } });
    expect(cached).toBe(first);

    // Tearing the store down must clear the singleton so a later mount
    // (e.g. re-entering the authenticated layout) starts from clean state
    // rather than inheriting stale realtime data.
    first.destroy();

    let afterDestroy!: RealtimeStore;
    render(Harness, {
      props: { onstore: (s: RealtimeStore) => (afterDestroy = s) },
    });
    expect(afterDestroy).not.toBe(first);
  });
});

describe("RealtimeStore recentEntries", () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it("recomputes once a minute, not on every clock tick", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-01-01T12:00:10Z"));
    const store = new RealtimeStore({
      url: "",
      reconnectAttempts: 0,
      reconnectDelay: 0,
      maxReconnectDelay: 0,
      pingTimeout: 0,
      pingInterval: 0,
    });
    const seen: unknown[] = [];
    const cleanup = $effect.root(() => {
      $effect(() => {
        seen.push(store.recentEntries);
      });
    });
    flushSync();

    for (const at of ["12:00:20", "12:00:40", "12:00:59"]) {
      vi.setSystemTime(new Date(`2026-01-01T${at}Z`));
      store.now = Date.now();
      flushSync();
    }
    expect(seen).toHaveLength(1);

    vi.setSystemTime(new Date("2026-01-01T12:01:01Z"));
    store.now = Date.now();
    flushSync();
    expect(seen).toHaveLength(2);

    cleanup();
    store.destroy();
  });
});
