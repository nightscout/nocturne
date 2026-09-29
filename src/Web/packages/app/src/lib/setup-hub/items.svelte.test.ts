import { describe, expect, it } from "vitest";
import { SetupHubItemKey } from "$api";
import {
  HUB_PAINTING_STOPS,
  setupHubItems,
  hubPaintingStop,
  setupHubItemBySlug,
} from "./items.svelte";

describe("hubPaintingStop", () => {
  it.each([
    [0, 6],
    [1, 5],
    [2, 4],
    [3, 3],
    [4, 2],
    [5, 1],
  ])("paints one stage per resolved item: %i open shows stop %i", (open, stop) => {
    expect(hubPaintingStop(open)).toBe(stop);
  });

  it("finishes the painting only when nothing is open", () => {
    expect(hubPaintingStop(0)).toBe(HUB_PAINTING_STOPS);
    expect(hubPaintingStop(1)).toBeLessThan(HUB_PAINTING_STOPS);
  });

  it("never shows blank paper, even with every item open", () => {
    expect(hubPaintingStop(6)).toBe(1);
  });
});

describe("setup hub item registry", () => {
  it("presents every item the server can list, each at its own page", () => {
    const slugs = Object.values(SetupHubItemKey).map((key) => setupHubItems()[key].slug);

    expect(new Set(slugs).size).toBe(slugs.length);
    for (const key of Object.values(SetupHubItemKey)) {
      expect(setupHubItemBySlug(setupHubItems()[key].slug)).toBe(key);
    }
    expect(setupHubItemBySlug("nope")).toBeUndefined();
  });

  it("describes each item in whole sentences for each way of speaking of the patient", () => {
    for (const view of Object.values(setupHubItems())) {
      expect(view.description({ kind: "named", name: "Sam" })).toContain("Sam");
      expect(view.description({ kind: "self" })).not.toContain("Sam");
      expect(view.description({ kind: "neutral" })).toMatch(/\.$/);
    }
  });
});
