import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

const reducedMotion = vi.hoisted(() => ({ value: false }));
vi.mock("@nocturne/watercolour", async () => ({
  Artwork: (await import("$lib/test-stubs/Artwork.test-stub.svelte")).default,
  hostSurface: () => "light",
  watchSurface: () => () => {},
  prefersReducedMotion: () => reducedMotion.value,
}));

import { fakePlayer } from "$lib/test-stubs/Artwork.test-stub.svelte";
import HubPainting from "./HubPainting.svelte";

const artwork = () => page.getByTestId("artwork");

beforeEach(() => {
  reducedMotion.value = false;
  fakePlayer.seeks = [];
  fakePlayer.plays = 0;
  Object.assign(fakePlayer.state, { mode: "live", motion: "full", progress: 0, playing: false });
});

describe("HubPainting", () => {
  it("shows the dawn ridges at its stop, as a sixth of the linear timeline", async () => {
    render(HubPainting, { stop: 3 });

    await expect.element(artwork()).toHaveAttribute("data-artwork", "hub-dawn-ridges");
    await expect.element(artwork()).toHaveAttribute("data-autoplay", "never");
    await expect.poll(() => fakePlayer.seeks).toEqual([0.5]);
    expect(fakePlayer.plays).toBe(0);
  });

  it("paints forward from a stop already seen to the next one", async () => {
    render(HubPainting, { stop: 4, from: 3 });

    await expect.poll(() => fakePlayer.plays).toBe(1);
    expect(fakePlayer.seeks).toEqual([0.5]);

    fakePlayer.state.progress = 4 / 6;

    await expect.poll(() => fakePlayer.seeks.at(-1)).toBe(4 / 6);
    expect(fakePlayer.state.playing).toBe(false);
  });

  it("advances when an item is resolved while it is on screen", async () => {
    const view = render(HubPainting, { stop: 2 });
    await expect.poll(() => fakePlayer.seeks).toEqual([2 / 6]);

    await view.rerender({ stop: 3 });

    await expect.poll(() => fakePlayer.plays).toBe(1);
  });

  it("jumps rather than paints under reduced motion, on the baked strip", async () => {
    reducedMotion.value = true;
    fakePlayer.state.motion = "reduced";

    render(HubPainting, { stop: 5, from: 2 });

    await expect.element(artwork()).toHaveAttribute("data-mode", "baked");
    await expect.poll(() => fakePlayer.seeks).toEqual([5 / 6]);
    expect(fakePlayer.plays).toBe(0);
  });

  it("stays out of sight when only the finished still can be shown", async () => {
    fakePlayer.state.mode = "static";

    render(HubPainting, { stop: 1 });

    await expect.element(artwork()).toHaveClass("hidden");
    expect(fakePlayer.seeks).toEqual([]);
  });
});
