import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import InitialReadsPrefetch from "./InitialReadsPrefetch.svelte";

describe("InitialReadsPrefetch", () => {
  it("puts the prefetch script in the head for a viewer of realtime data", () => {
    const { head } = render(InitialReadsPrefetch, { props: { enabled: true } });

    expect(head).toMatch(/<script>[\s\S]*__nocturneInitialReads[\s\S]*<\/script>/);
    expect(head).not.toContain('type="module"');
  });

  it("puts nothing in the head otherwise", () => {
    const { head } = render(InitialReadsPrefetch, { props: { enabled: false } });

    expect(head).not.toContain("__nocturneInitialReads");
  });
});
