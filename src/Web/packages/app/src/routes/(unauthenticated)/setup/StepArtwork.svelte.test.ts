import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@nocturne/watercolour", async () => ({
  Artwork: (await import("$lib/test-stubs/Artwork.test-stub.svelte")).default,
  hostSurface: () => "light",
  watchSurface: () => () => {},
}));

import { fakePlayer } from "$lib/test-stubs/Artwork.test-stub.svelte";
import StepArtwork from "./StepArtwork.svelte";

const artwork = () => page.getByTestId("artwork");

describe("StepArtwork", () => {
  beforeEach(() => {
    fakePlayer.seeks = [];
    fakePlayer.state.motion = "full";
  });

  it("plays a step's artwork once on its own", async () => {
    render(StepArtwork, { art: "source" });

    await expect.element(artwork()).toHaveAttribute("data-artwork", "plug");
    await expect.element(artwork()).toHaveAttribute("data-autoplay", "once");
    expect(fakePlayer.seeks).toEqual([]);
  });

  it("reveals the import artwork as far as the import has got", async () => {
    const view = render(StepArtwork, { art: "import", progress: 0.25 });

    await expect.element(artwork()).toHaveAttribute("data-autoplay", "never");
    await expect.poll(() => fakePlayer.seeks.at(-1)).toBe(0.25);

    await view.rerender({ art: "import", progress: 0.504 });
    await expect.poll(() => fakePlayer.seeks.at(-1)).toBe(0.5);
  });

  it("does not seek within a whole percent", async () => {
    const view = render(StepArtwork, { art: "import", progress: 0.3 });
    await expect.poll(() => fakePlayer.seeks).toEqual([0.3]);

    await view.rerender({ art: "import", progress: 0.3004 });

    expect(fakePlayer.seeks).toEqual([0.3]);
  });

  // Reduced motion shows the finished artwork; rewinding it to the import's
  // progress would animate it after all.
  it("leaves a reduced-motion artwork finished", async () => {
    fakePlayer.state.motion = "reduced";

    render(StepArtwork, { art: "import", progress: 0.4 });

    await expect.element(artwork()).toBeInTheDocument();
    expect(fakePlayer.seeks).toEqual([]);
  });
});
