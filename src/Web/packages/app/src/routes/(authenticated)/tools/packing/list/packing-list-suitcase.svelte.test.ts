import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { encodeBase64Utf8 } from "$lib/utils";
import type { LiveClock, MountOptions, PlayerState, TimedOp, WasmModule } from "@nocturne/watercolour";

const reducedMotion = vi.hoisted(() => ({ current: false }));

/**
 * The live suitcase's engine, as the page drives it. Like the real player it
 * builds the scene once the engine has loaded, then reports ready, then live;
 * `build` and `ready` let a test hold either step back.
 */
const live = vi.hoisted(() => ({
  /** The mode the engine settles on: `live`, or `none` where nothing can paint. */
  mode: "live" as "live" | "none",
  /** The player's resolved motion: reduced under the OS setting or the app's "still" presentation. */
  motion: "full" as "full" | "reduced",
  autoBuild: true,
  autoReady: true,
  scenes: [] as string[],
  paints: [] as { ops: readonly TimedOp[]; clock: LiveClock }[],
  calls: [] as string[],
  build: () => {},
  ready: () => {},
  /** Tears the player down and mounts a new one, as a presentation change does. */
  rebuild: () => {},
  /** The live backend fails mid-session and the player settles on `none`. */
  fault: () => {},
}));

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

vi.mock("@nocturne/watercolour", async (importOriginal) => {
  const watercolour = await importOriginal<typeof import("@nocturne/watercolour")>();
  const donor = JSON.stringify({
    version: 1,
    palette: {
      entries: ["base_wash", "shadow", "accent", "glow"].map((role) => ({ role, pigment: {} })),
    },
    timeline: {
      total_ticks: 340,
      events: [
        {
          at_tick: 0,
          op: { set_mask: { mask: { polygon: { points: [[0.12, 0.34], [0.88, 0.34], [0.88, 0.84], [0.12, 0.84]], feather: 0.01 } } } },
        },
      ],
    },
  });
  const module = { catalogueScene: () => donor } as unknown as WasmModule;
  const player = {
    get state() {
      return { mode: "live", motion: live.motion } as PlayerState;
    },
    play: () => live.calls.push("play"),
    finishImmediately: () => live.calls.push("finishImmediately"),
    paint: (ops: readonly TimedOp[], clock: LiveClock) => {
      live.calls.push("paint");
      live.paints.push({ ops, clock });
    },
  };
  const later = (step: () => void) => queueMicrotask(step);

  function mount(options: MountOptions) {
    let cleanup: (() => void) | void;
    let disposed = false;
    if (live.mode === "none") {
      later(() => options.onStateChange?.({ mode: "none" } as PlayerState));
      return { dispose: () => {} };
    }
    live.ready = () => {
      if (disposed) return;
      cleanup = options.onReady?.(player as never);
      options.onStateChange?.({ mode: "live", motion: live.motion } as PlayerState);
    };
    live.build = () => {
      if (disposed) return;
      live.scenes.push(options.scene!(module, 120, 120, 2));
      if (live.autoReady) live.ready();
    };
    live.fault = () => options.onStateChange?.({ mode: "none" } as PlayerState);
    if (live.autoBuild) later(() => live.build());
    return {
      dispose: () => {
        disposed = true;
        cleanup?.();
      },
    };
  }

  return {
    ...watercolour,
    Artwork: (await import("./SpyArtwork.test.svelte")).default,
    mountPlayer: (_frame: HTMLElement, _canvas: HTMLCanvasElement, options: MountOptions) => {
      let current = mount(options);
      live.rebuild = () => {
        current.dispose();
        current = mount(options);
      };
      return () => current.dispose();
    },
  };
});

import { settle, spyPlayer } from "./SpyArtwork.test.svelte";
import PackingListPage from "./+page.svelte";

function listUrl(items: Array<{ c: string; l: string; q: number; p?: 1 }>) {
  const encoded = encodeURIComponent(encodeBase64Utf8(JSON.stringify(items)));
  return new URL(`http://localhost/tools/packing/list?d=${encoded}`) as typeof pageState.url;
}

const packed = (label: string) => page.getByRole("checkbox", { name: `Packed: ${label}` });
const removeButton = (label: string) => page.getByRole("button", { name: `Remove ${label}` });

type Brush = { brush: { pigment: number; concentration: number } };
const kinds = (ops: readonly TimedOp[]) => ops.map(({ op }) => (typeof op === "string" ? op : Object.keys(op)[0]));
const lastPaint = () => live.paints.at(-1)!.ops;
const brushes = (ops: readonly { op: unknown }[]) =>
  ops.flatMap(({ op }) => (typeof op === "object" && op !== null && "brush" in op ? [(op as Brush).brush] : []));
/** Indigo, the donor's `shadow`: only the strap and handle are painted in it. */
const HARDWARE = 1;
const hasHardware = (ops: readonly { op: unknown }[]) => brushes(ops).some((b) => b.pigment === HARDWARE);
const sceneEvents = (index: number) => (JSON.parse(live.scenes[index]!) as { timeline: { events: { op: unknown }[] } }).timeline.events;
/** Each replayed pack starts by restoring the silhouette. */
const replayedBands = (index: number) =>
  sceneEvents(index).filter(({ op }) => typeof op === "object" && op !== null && "set_mask" in op).length;

describe("packing list suitcase", () => {
  beforeEach(() => {
    reducedMotion.current = false;
    vi.clearAllMocks();
    Object.assign(live, { mode: "live", motion: "full", autoBuild: true, autoReady: true, scenes: [], paints: [], calls: [] });
    pageState.url = listUrl([
      { c: "Supplies", l: "Test strips", q: 2, p: 1 },
      { c: "Supplies", l: "Pen needles", q: 10 },
      { c: "Clothes", l: "Socks", q: 3 },
    ]);
  });

  it("replays what is already packed and leaves the reveal to the player", async () => {
    render(PackingListPage, {});
    await expect.poll(() => live.scenes.length).toBe(1);
    expect(replayedBands(0)).toBe(1);
    expect(hasHardware(sceneEvents(0))).toBe(false);
    expect(live.calls).toEqual([]);
  });

  it("replays a fully packed list with its hardware on", async () => {
    pageState.url = listUrl([
      { c: "Supplies", l: "Test strips", q: 2, p: 1 },
      { c: "Clothes", l: "Socks", q: 3, p: 1 },
    ]);
    render(PackingListPage, {});
    await expect.poll(() => live.scenes.length).toBe(1);
    expect(replayedBands(0)).toBe(2);
    expect(hasHardware(sceneEvents(0))).toBe(true);
  });

  it("paints a band for each pack and lifts the lowest on an unpack, on the suitcase's clock", async () => {
    render(PackingListPage, {});
    await packed("Pen needles").click();
    await expect.poll(() => live.paints.length).toBe(1);
    expect(kinds(lastPaint())).toContain("brush");
    expect(hasHardware(lastPaint())).toBe(false);
    expect(live.paints[0]!.clock.ticksPerSecond).toBeGreaterThan(0);

    await packed("Pen needles").click();
    await expect.poll(() => live.paints.length).toBe(2);
    expect(kinds(lastPaint())).toEqual(["lift"]);
  });

  it("puts the hardware on when the list is complete, by packing or by removing what is left", async () => {
    render(PackingListPage, {});
    await packed("Pen needles").click();
    await packed("Socks").click();
    await expect.poll(() => live.paints.length).toBe(2);
    expect(hasHardware(lastPaint())).toBe(true);

    await packed("Socks").click();
    await expect.poll(() => live.paints.length).toBe(3);
    await removeButton("Socks").click();
    await expect.poll(() => live.paints.length).toBe(4);
    expect(hasHardware(lastPaint())).toBe(true);
  });

  it("lifts a band when a packed item is removed, and paints nothing when an unpacked one is", async () => {
    render(PackingListPage, {});
    await removeButton("Pen needles").click();
    await expect.element(removeButton("Pen needles")).not.toBeInTheDocument();
    expect(live.paints).toEqual([]);

    await removeButton("Test strips").click();
    await expect.poll(() => live.paints.length).toBe(1);
    expect(kinds(lastPaint())).toEqual(["lift"]);
  });

  it("glazes the full suitcase for an item added and packed after it was complete", async () => {
    render(PackingListPage, {});
    await packed("Pen needles").click();
    await packed("Socks").click();
    await expect.poll(() => live.paints.length).toBe(2);

    await page.getByRole("button", { name: "Add custom item" }).click();
    await page.getByPlaceholder("Item name...").fill("Charger");
    await userEvent.keyboard("{Enter}");
    await packed("Charger").click();
    await expect.poll(() => live.paints.length).toBe(3);
    expect(hasHardware(lastPaint())).toBe(false);
    expect(Math.max(...brushes(lastPaint()).map((b) => b.concentration))).toBeLessThan(0.2);
  });

  it("replays a pack made before the scene was built, rather than painting it", async () => {
    live.autoBuild = false;
    render(PackingListPage, {});
    await packed("Pen needles").click();
    await expect.element(packed("Pen needles")).toBeChecked();
    live.build();
    expect(replayedBands(0)).toBe(2);
    expect(live.paints).toEqual([]);
  });

  it("hands over packs made while the player was getting ready once it is", async () => {
    live.autoReady = false;
    render(PackingListPage, {});
    await expect.poll(() => live.scenes.length).toBe(1);
    await packed("Pen needles").click();
    await expect.element(packed("Pen needles")).toBeChecked();
    expect(live.paints).toEqual([]);

    live.ready();
    expect(live.paints).toHaveLength(1);
    expect(kinds(live.paints[0]!.ops)).toContain("brush");
  });

  it("rebuilds from what is still packed, without painting it twice", async () => {
    render(PackingListPage, {});
    await packed("Pen needles").click();
    await packed("Pen needles").click();
    await expect.poll(() => live.paints.length).toBe(2);

    live.rebuild();
    await expect.poll(() => live.scenes.length).toBe(2);
    expect(replayedBands(1)).toBe(1);
    expect(live.paints).toHaveLength(2);

    await packed("Socks").click();
    await expect.poll(() => live.paints.length).toBe(3);
  });

  it("finishes each paint at once when the player's motion is reduced", async () => {
    live.motion = "reduced";
    render(PackingListPage, {});
    await expect.poll(() => live.scenes.length).toBe(1);
    await packed("Pen needles").click();
    await expect.poll(() => live.calls).toEqual(["paint", "finishImmediately"]);
  });

  describe("without a live engine", () => {
    const seeks = () => spyPlayer.seekTo.mock.calls.map(([at]) => at);
    const PAINT_END = 0.2;

    it("seeks the catalogue suitcase's reveal by the share packed, and plays the settle when complete", async () => {
      live.mode = "none";
      render(PackingListPage, {});
      await expect.poll(() => seeks().at(-1)).toBeCloseTo(PAINT_END / 3, 10);
      await packed("Pen needles").click();
      await expect.poll(() => seeks().at(-1)).toBeCloseTo((PAINT_END * 2) / 3, 10);
      await packed("Socks").click();
      await expect.poll(() => spyPlayer.play.mock.calls.length).toBe(1);
      expect(seeks().every((at) => at <= PAINT_END)).toBe(true);
      expect(live.paints).toEqual([]);
    });

    it("takes over at the packed share when the live suitcase fails mid-session, and paints nothing more live", async () => {
      render(PackingListPage, {});
      await packed("Pen needles").click();
      await expect.poll(() => live.paints.length).toBe(1);

      live.fault();
      await expect.poll(() => seeks().at(-1)).toBeCloseTo((PAINT_END * 2) / 3, 10);
      await packed("Socks").click();
      await expect.poll(() => spyPlayer.play.mock.calls.length).toBe(1);
      expect(live.paints).toHaveLength(1);
    });

    it("shows the plain icon when the catalogue suitcase cannot paint either", async () => {
      live.mode = "none";
      render(PackingListPage, {});
      await expect.poll(() => spyPlayer.seekTo.mock.calls.length).toBeGreaterThan(0);
      await expect.element(page.getByTestId("packing-icon")).not.toBeInTheDocument();

      settle("none");

      await expect.element(page.getByTestId("packing-icon")).toBeInTheDocument();
    });
  });

  it("shows the plain icon for an empty list", async () => {
    pageState.url = listUrl([]);
    render(PackingListPage, {});

    await expect.element(page.getByTestId("packing-icon")).toBeInTheDocument();
    // The empty state paints a suitcase of its own; the header's is the one that must go.
    await expect.element(page.getByTestId("packing-header").getByTestId("suitcase")).not.toBeInTheDocument();
  });
});
