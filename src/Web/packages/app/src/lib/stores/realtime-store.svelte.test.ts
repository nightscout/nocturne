import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import Harness from "./realtime-store-harness.svelte";
import type { RealtimeStore } from "./realtime-store.svelte";

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
