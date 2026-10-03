import { describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { engine } from "$lib/test-stubs/Watercolour.test-stub.svelte";
import GlucoseTileWashHarness from "./GlucoseTileWashHarness.test.svelte";

vi.mock("@nocturne/watercolour", async (importOriginal) => {
  const fake = await import("$lib/test-stubs/Watercolour.test-stub.svelte");
  return {
    ...(await importOriginal<typeof import("@nocturne/watercolour")>()),
    Artwork: fake.default,
    mountPlayer: fake.mountPlayer,
    bloomScene: fake.bloomScene,
  };
});

const FIVE_MINUTES_MS = 5 * 60_000;
// The wash keeps its last reading across mounts, so each test paints readings of its own.
let nextMills = 1_700_000_000_000;
const newReading = () => (nextMills += FIVE_MINUTES_MS);

const bloom = () => page.getByTestId("glucose-tile-bloom");
const greyWash = () => page.getByTestId("artwork-wash");
const priorFill = () => page.getByTestId("glucose-tile-prior-fill");

function paintBloom(progress: number) {
  for (const painting of engine.live()) if (painting.scene) painting.paint(progress);
}

/** A tile that has shown the loading skeleton and then a settled reading in `variant`. */
async function settledTile(variant: "high" | "low") {
  const view = render(GlucoseTileWashHarness, { mills: undefined, variant: "neutral", isLoading: true });
  await view.rerender({ mills: newReading(), variant, isLoading: false });
  await expect.element(bloom()).toBeInTheDocument();
  paintBloom(0.7);
  return view;
}

describe("GlucoseTileWash", () => {
  it("blooms the first reading over the loading skeleton", async () => {
    const view = render(GlucoseTileWashHarness, { mills: undefined, variant: "neutral", isLoading: true });

    await view.rerender({ mills: newReading(), variant: "high", isLoading: false });

    await expect.element(bloom()).toBeInTheDocument();
    await expect.element(priorFill()).toHaveClass("bg-accent");
    await expect.element(greyWash()).not.toBeInTheDocument();
  });

  it("blooms over the neutral fill when the first reading showed before its status", async () => {
    const view = render(GlucoseTileWashHarness, { mills: undefined, variant: "neutral", isLoading: true });
    const mills = newReading();
    await view.rerender({ mills, variant: "neutral", isLoading: false });

    await view.rerender({ mills, variant: "high" });

    await expect.element(bloom()).toBeInTheDocument();
    await expect.element(priorFill()).toHaveClass("bg-muted");
  });

  it("keeps the grey wash for a new reading in the same range", async () => {
    const view = await settledTile("high");

    await view.rerender({ mills: newReading(), variant: "high" });

    await expect.element(greyWash()).toBeInTheDocument();
    await expect.element(bloom()).not.toBeInTheDocument();
    await expect.element(priorFill()).not.toBeInTheDocument();
  });

  it("blooms a reading in another range over the old range's fill", async () => {
    const view = await settledTile("high");

    await view.rerender({ mills: newReading(), variant: "low" });

    await expect.element(bloom()).toBeInTheDocument();
    await expect.element(priorFill()).toHaveClass("bg-glucose-high");
    await expect.element(greyWash()).not.toBeInTheDocument();
  });

  it("blooms over the neutral fill a stale tile showed", async () => {
    const view = await settledTile("high");
    await view.rerender({ isStale: true });
    await expect.element(bloom()).not.toBeInTheDocument();

    await view.rerender({ mills: newReading(), variant: "high", isStale: false });

    await expect.element(bloom()).toBeInTheDocument();
    await expect.element(priorFill()).toHaveClass("bg-muted");
  });

  it("holds the old fill until the bloom covers the tile, then lets it go", async () => {
    const view = await settledTile("high");
    await view.rerender({ mills: newReading(), variant: "low" });
    await expect.element(bloom()).toBeInTheDocument();

    paintBloom(0.5);
    await expect.element(priorFill()).toHaveClass("bg-glucose-high");
    await expect.element(priorFill()).toHaveStyle({ opacity: "1" });

    paintBloom(0.7);
    await expect.element(priorFill(), { timeout: 4000 }).toHaveStyle({ opacity: "0" });
    await expect.element(bloom()).toBeInTheDocument();
  });
});
