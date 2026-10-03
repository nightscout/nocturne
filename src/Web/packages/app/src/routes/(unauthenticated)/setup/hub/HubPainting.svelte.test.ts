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
  Object.assign(fakePlayer.state, { mode: "live", motion: "full", progress: 0, playing: false, seeking: false });
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

  it("stays out of sight when only the finished still can be shown, and says so at once", async () => {
    fakePlayer.state.mode = "static";
    const onpainted = vi.fn();

    render(HubPainting, { stop: 1, onpainted });

    await expect.element(artwork()).toHaveClass("hidden");
    expect(fakePlayer.seeks).toEqual([]);
    expect(onpainted).toHaveBeenCalledWith(1);
  });

  it("reports a stop only once it is on the canvas", async () => {
    const onpainted = vi.fn();
    render(HubPainting, { stop: 6, from: 5, onpainted });

    await expect.poll(() => fakePlayer.plays).toBe(1);
    expect(onpainted).not.toHaveBeenCalled();

    fakePlayer.state.progress = 1;

    await expect.poll(() => onpainted.mock.calls).toEqual([[6]]);
  });

  it("does not report a stop while its target is waiting for presentation", async () => {
    const onpainted = vi.fn();
    fakePlayer.state.seeking = true;
    render(HubPainting, { stop: 3, onpainted });
    await expect.poll(() => fakePlayer.seeks).toEqual([0.5]);
    await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
    expect(onpainted).not.toHaveBeenCalled();
    fakePlayer.state.seeking = false;
    await expect.poll(() => onpainted.mock.calls).toEqual([[3]]);
  });

  it("shows half the first stage when nothing is resolved", async () => {
    render(HubPainting, { stop: 0.5 });

    await expect.poll(() => fakePlayer.seeks).toEqual([0.5 / 6]);
  });
});
