import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import type { PlayerState } from "@nocturne/watercolour";
import { engine } from "$lib/test-stubs/Watercolour.test-stub.svelte";
import GlucoseTileBloom from "./GlucoseTileBloom.svelte";

vi.mock("@nocturne/watercolour", async () => {
  const fake = await import("$lib/test-stubs/Watercolour.test-stub.svelte");
  return { mountPlayer: fake.mountPlayer, bloomScene: fake.bloomScene };
});

const TOKEN = "--test-range";

beforeEach(() => {
  document.documentElement.style.setProperty(TOKEN, "rgb(255, 0, 0)");
});

afterEach(() => {
  document.documentElement.style.removeProperty(TOKEN);
});

function bloom(props: Partial<{ seed: number; delta: number; onprogress: (progress: number) => void; onstatechange: (state: PlayerState) => void }> = {}) {
  return render(GlucoseTileBloom, {
    seed: 7,
    delta: 15,
    token: TOKEN,
    onprogress: () => {},
    onstatechange: () => {},
    ...props,
  });
}

describe("GlucoseTileBloom", () => {
  it("paints the reading in its range token's colour on a line angled by its trend", async () => {
    bloom({ seed: 7, delta: -7.5 });

    await expect.poll(() => engine.live().length).toBe(1);
    expect(engine.live()[0]!.scene).toMatchObject({ seed: 7, slope: -0.5, colour: [1, 0, 0] });
  });

  it("blends the engine's simulation ticks so the bloom spreads smoothly between them", async () => {
    bloom();

    await expect.poll(() => engine.live()[0]?.blendTicks).toBe(true);
  });

  it("caps the line's angle at a 15 mg/dL change", async () => {
    bloom({ delta: 40 });

    await expect.poll(() => engine.live()[0]?.scene).toMatchObject({ slope: 1 });
  });

  it("keeps painting the same bloom when the trend or the host's callbacks change", async () => {
    const view = bloom({ delta: 15 });
    await expect.poll(() => engine.live().length).toBe(1);
    const painting = engine.live()[0];

    await view.rerender({ delta: -15, onprogress: () => {}, onstatechange: () => {} });

    expect(engine.live()).toEqual([painting]);
  });

  it("reports the engine's progress and state to the host it currently has", async () => {
    const view = bloom();
    await expect.poll(() => engine.live().length).toBe(1);
    const onprogress = vi.fn();
    const onstatechange = vi.fn();
    await view.rerender({ onprogress, onstatechange });

    engine.live()[0]!.paint(0.7);

    expect(onprogress).toHaveBeenCalledWith(0.7);
    expect(onstatechange).toHaveBeenCalledWith(expect.objectContaining({ progress: 0.7, finished: false }));
  });

  it("releases its painting when it unmounts", async () => {
    const view = bloom();
    await expect.element(page.getByTestId("glucose-tile-bloom")).toBeInTheDocument();
    await expect.poll(() => engine.live().length).toBe(1);

    view.unmount();

    expect(engine.live()).toEqual([]);
  });
});
