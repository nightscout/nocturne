import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { encodeBase64Utf8 } from "$lib/utils";

const reducedMotion = vi.hoisted(() => ({ current: false }));

vi.mock("$app/navigation", () => ({
  goto: vi.fn((url: string) => {
    pageState.url = new URL(url) as typeof pageState.url;
    return Promise.resolve();
  }),
}));

vi.mock("svelte/motion", async (importOriginal) => ({
  ...(await importOriginal<typeof import("svelte/motion")>()),
  prefersReducedMotion: reducedMotion,
}));

vi.mock("@nocturne/watercolour", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@nocturne/watercolour")>()),
  Artwork: (await import("./SpyArtwork.test.svelte")).default,
}));

import { settle, spyPlayer } from "./SpyArtwork.test.svelte";
import PackingListPage from "./+page.svelte";

const PAINT_END = 0.2;

function listUrl(items: Array<{ c: string; l: string; q: number; p?: 1 }>) {
  const encoded = encodeURIComponent(encodeBase64Utf8(JSON.stringify(items)));
  return new URL(`http://localhost/tools/packing/list?d=${encoded}`) as typeof pageState.url;
}

const packed = (label: string) => page.getByRole("checkbox", { name: `Packed: ${label}` });
const seeks = () => spyPlayer.seek.mock.calls.map(([at]) => at);
const lastSeek = () => seeks().at(-1);

describe("packing list suitcase", () => {
  beforeEach(() => {
    reducedMotion.current = false;
    vi.clearAllMocks();
    pageState.url = listUrl([
      { c: "Supplies", l: "Test strips", q: 2 },
      { c: "Supplies", l: "Pen needles", q: 10 },
    ]);
  });

  it("paints in over the brushwork as items are packed and jumps back on an unpack", async () => {
    render(PackingListPage, {});
    await expect.poll(lastSeek).toBe(0);

    await packed("Test strips").click();
    await expect.poll(lastSeek).toBeCloseTo(PAINT_END / 2);
    const rising = seeks();
    expect(rising.every((at, i) => i === 0 || at >= rising[i - 1])).toBe(true);

    const before = spyPlayer.seek.mock.calls.length;
    await packed("Test strips").click();
    await expect.poll(lastSeek).toBe(0);
    expect(seeks().slice(before)).toEqual([0]);

    expect(seeks().every((at) => at <= PAINT_END)).toBe(true);
    expect(spyPlayer.play).not.toHaveBeenCalled();
  });

  it("plays the settle once everything is packed, and not again on a re-render", async () => {
    render(PackingListPage, {});
    await packed("Test strips").click();
    await packed("Pen needles").click();

    await expect.poll(() => spyPlayer.play.mock.calls.length).toBe(1);
    expect(seeks().every((at) => at <= PAINT_END)).toBe(true);

    const label = page.getByRole("textbox").first();
    await label.fill("Test strips (spare)");
    label.element().dispatchEvent(new FocusEvent("blur"));
    await expect.element(page.getByRole("checkbox", { name: "Packed: Test strips (spare)" })).toBeChecked();

    expect(spyPlayer.play).toHaveBeenCalledTimes(1);
    expect(spyPlayer.finishImmediately).not.toHaveBeenCalled();
  });

  it("finishes without animating under reduced motion", async () => {
    reducedMotion.current = true;
    render(PackingListPage, {});
    await packed("Test strips").click();
    await expect.poll(lastSeek).toBeCloseTo(PAINT_END / 2);
    expect(spyPlayer.finishImmediately).not.toHaveBeenCalled();

    await packed("Pen needles").click();

    await expect.poll(() => spyPlayer.finishImmediately.mock.calls.length).toBe(1);
    expect(spyPlayer.play).not.toHaveBeenCalled();
  });

  it("shows the plain icon when the suitcase cannot paint", async () => {
    render(PackingListPage, {});
    await expect.element(page.getByTestId("suitcase")).toBeInTheDocument();
    await expect.element(page.getByTestId("packing-icon")).not.toBeInTheDocument();

    settle("none");

    await expect.element(page.getByTestId("packing-icon")).toBeInTheDocument();
    await expect.element(page.getByTestId("suitcase")).not.toBeInTheDocument();
  });

  it("shows the plain icon for an empty list", async () => {
    pageState.url = listUrl([]);
    render(PackingListPage, {});

    await expect.element(page.getByTestId("packing-icon")).toBeInTheDocument();
    await expect.element(page.getByTestId("suitcase")).not.toBeInTheDocument();
  });
});
