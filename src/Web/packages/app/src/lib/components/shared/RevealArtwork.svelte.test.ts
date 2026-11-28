import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

function deferred<T = void>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

const engine = vi.hoisted(() => ({
  presentation: "animated" as "animated" | "still" | "off",
  reducedMotion: false,
  wanted: true,
  ready: false,
  booting: null as Promise<void> | null,
  warms: 0,
  warm: undefined as { promise: Promise<boolean>; resolve: (value: boolean) => void } | undefined,
  notBooting: undefined as { promise: Promise<void>; resolve: () => void } | undefined,
}));

vi.mock("#lib/stores/illustrations.svelte.js", () => ({
  watercolourWanted: () => engine.wanted,
}));

vi.mock("water-ui", async () => ({
  Artwork: (await import("#lib/test-stubs/Artwork.test-stub.svelte")).default,
  getPresentation: () => engine.presentation,
  prefersReducedMotion: () => engine.presentation !== "animated" || engine.reducedMotion,
  WARM_IDLE_TIMEOUT_MS: 0,
  getEngineHost: () => ({
    get ready() {
      return engine.ready;
    },
    get booting() {
      return engine.booting;
    },
    warm: () => {
      engine.warms += 1;
      return engine.warm!.promise;
    },
    whenNotBooting: () => engine.notBooting?.promise ?? Promise.resolve(),
  }),
}));

import RevealArtwork from "./RevealArtwork.svelte";

const artwork = () => page.getByTestId("artwork");
const mounted = () => document.querySelector("[data-testid=artwork]") !== null;

describe("RevealArtwork", () => {
  beforeEach(() => {
    Object.assign(engine, {
      presentation: "animated",
      reducedMotion: false,
      wanted: true,
      ready: false,
      booting: null,
      warms: 0,
      warm: deferred<boolean>(),
      notBooting: undefined,
    });
  });

  it("holds its slot while the engine warms, then mounts the artwork", async () => {
    const view = render(RevealArtwork, { artwork: "plug", class: "size-56" });

    await expect.poll(() => engine.warms).toBe(1);
    expect(mounted()).toBe(false);
    expect(view.container.querySelector("div.size-56")).not.toBeNull();

    engine.ready = true;
    engine.warm!.resolve(true);

    await expect.element(artwork()).toHaveAttribute("data-artwork", "plug");
  });

  it("waits for a boot still clearing after the warm resolves", async () => {
    engine.notBooting = deferred();
    render(RevealArtwork, { artwork: "plug" });
    await expect.poll(() => engine.warms).toBe(1);

    engine.warm!.resolve(true);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(mounted()).toBe(false);

    engine.notBooting.resolve();
    await expect.element(artwork()).toBeInTheDocument();
  });

  it("mounts the artwork when there is no GPU to warm", async () => {
    render(RevealArtwork, { artwork: "plug" });
    await expect.poll(() => engine.warms).toBe(1);

    engine.warm!.resolve(false);

    await expect.element(artwork()).toBeInTheDocument();
  });

  it("mounts at once when the engine is already up", async () => {
    engine.ready = true;

    render(RevealArtwork, { artwork: "plug" });

    await expect.element(artwork()).toBeInTheDocument();
    expect(engine.warms).toBe(0);
  });

  it("warms rather than mount against a boot in progress", async () => {
    engine.ready = true;
    engine.booting = new Promise(() => {});

    render(RevealArtwork, { artwork: "plug" });

    await expect.poll(() => engine.warms).toBe(1);
    expect(mounted()).toBe(false);
  });

  it("drops a warm that settles after it unmounts", async () => {
    const view = render(RevealArtwork, { artwork: "plug" });
    await expect.poll(() => engine.warms).toBe(1);

    view.unmount();
    engine.warm!.resolve(true);
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(mounted()).toBe(false);
  });

  it.each([
    ["under still", () => (engine.presentation = "still")],
    ["under off", () => (engine.presentation = "off")],
    ["when the browser saves data", () => (engine.wanted = false)],
    ["under reduced motion", () => (engine.reducedMotion = true)],
  ])("mounts at once %s, booting nothing", async (_, arrange) => {
    arrange();

    render(RevealArtwork, { artwork: "plug" });

    await expect.element(artwork()).toBeInTheDocument();
    expect(engine.warms).toBe(0);
  });

  it("mounts at once for a mode the engine does not decide", async () => {
    render(RevealArtwork, { artwork: "plug", mode: "baked" });

    await expect.element(artwork()).toHaveAttribute("data-mode", "baked");
    expect(engine.warms).toBe(0);
  });

  it("warms under reduced motion when the artwork plays in full regardless", async () => {
    engine.reducedMotion = true;

    render(RevealArtwork, { artwork: "plug", motion: "full" });

    await expect.poll(() => engine.warms).toBe(1);
    expect(mounted()).toBe(false);
  });
});
