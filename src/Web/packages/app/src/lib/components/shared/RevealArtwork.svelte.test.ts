import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

const engine = vi.hoisted(() => ({
  presentation: "animated" as "animated" | "still" | "off",
  ready: false,
  booting: null as Promise<void> | null,
  warms: 0,
  finishWarm: undefined as (() => void) | undefined,
}));

vi.mock("water-ui", async () => ({
  Artwork: (await import("#lib/test-stubs/Artwork.test-stub.svelte")).default,
  getPresentation: () => engine.presentation,
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
      return new Promise<boolean>((resolve) => {
        engine.finishWarm = () => {
          engine.ready = true;
          resolve(true);
        };
      });
    },
    whenNotBooting: () => Promise.resolve(),
  }),
}));

import RevealArtwork from "./RevealArtwork.svelte";

const artwork = () => page.getByTestId("artwork");

describe("RevealArtwork", () => {
  beforeEach(() => {
    Object.assign(engine, { presentation: "animated", ready: false, booting: null, warms: 0, finishWarm: undefined });
  });

  it("holds its slot while the engine warms, then mounts the artwork", async () => {
    const view = render(RevealArtwork, { artwork: "plug", class: "size-56" });

    await expect.poll(() => engine.warms).toBe(1);
    expect(view.container.querySelector("[data-testid=artwork]")).toBeNull();
    expect(view.container.querySelector("div.size-56")).not.toBeNull();

    engine.finishWarm?.();

    await expect.element(artwork()).toHaveAttribute("data-artwork", "plug");
  });

  it("mounts at once when the engine is already up", async () => {
    engine.ready = true;

    render(RevealArtwork, { artwork: "plug" });

    await expect.element(artwork()).toBeInTheDocument();
    expect(engine.warms).toBe(0);
  });

  it("waits out a boot already in progress", async () => {
    engine.ready = true;
    engine.booting = new Promise(() => {});

    render(RevealArtwork, { artwork: "plug" });

    await expect.poll(() => engine.warms).toBe(1);
    expect(document.querySelector("[data-testid=artwork]")).toBeNull();
  });

  it.each(["still", "off"] as const)("mounts at once under %s, which boots nothing", async (presentation) => {
    engine.presentation = presentation;

    render(RevealArtwork, { artwork: "plug" });

    await expect.element(artwork()).toBeInTheDocument();
    expect(engine.warms).toBe(0);
  });
});
