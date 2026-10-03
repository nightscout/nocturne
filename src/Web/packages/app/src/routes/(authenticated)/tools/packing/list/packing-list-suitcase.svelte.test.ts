import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { encodeBase64Utf8 } from "$lib/utils";
import type { LiveClock, MountOptions, PlayerState, TimedOp, WasmModule } from "@nocturne/watercolour";

const reducedMotion = vi.hoisted(() => ({ current: false }));

/** The live suitcase's player, as the page drives it. */
const live = vi.hoisted(() => ({
  /** The mode the fake engine settles on: `live`, or `none` where nothing can paint. */
  mode: "live" as "live" | "none",
  /** Whether the player is ready as soon as it mounts; otherwise `ready()` hands it over. */
  readyAtMount: true,
  scenes: [] as string[],
  paints: [] as { ops: readonly TimedOp[]; clock: LiveClock }[],
  calls: [] as string[],
  ready: () => {},
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
    play: () => live.calls.push("play"),
    finishImmediately: () => live.calls.push("finishImmediately"),
    paint: (ops: readonly TimedOp[], clock: LiveClock) => {
      live.calls.push("paint");
      live.paints.push({ ops, clock });
    },
  };
  return {
    ...watercolour,
    Artwork: (await import("./SpyArtwork.test.svelte")).default,
    mountPlayer: (_frame: HTMLElement, _canvas: HTMLCanvasElement, options: MountOptions) => {
      if (live.mode === "none") {
        queueMicrotask(() => options.onStateChange?.({ mode: "none" } as PlayerState));
        return () => {};
      }
      live.scenes.push(options.scene!(module, 120, 120, 2));
      let cleanup: (() => void) | void;
      live.ready = () => {
        cleanup = options.onReady?.(player as never);
      };
      options.onStateChange?.({ mode: "live" } as PlayerState);
      if (live.readyAtMount) live.ready();
      return () => cleanup?.();
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

type Op = Record<string, Record<string, unknown>> | string;
const kinds = (ops: readonly TimedOp[]) => ops.map(({ op }) => (typeof op === "string" ? op : Object.keys(op)[0]));
const lastPaint = () => live.paints.at(-1)!.ops;
const brushes = (ops: readonly TimedOp[]) =>
  ops.flatMap(({ op }) => (typeof op === "object" && "brush" in op ? [(op as Op & { brush: { pigment: number } }).brush] : []));
/** Indigo, the donor's `shadow`: only the strap and handle are painted in it. */
const HARDWARE = 1;
const hasHardware = (ops: readonly TimedOp[]) => brushes(ops).some((b) => b.pigment === HARDWARE);

describe("packing list suitcase", () => {
  beforeEach(() => {
    reducedMotion.current = false;
    vi.clearAllMocks();
    Object.assign(live, { mode: "live", readyAtMount: true, scenes: [], paints: [], calls: [] });
    pageState.url = listUrl([
      { c: "Supplies", l: "Test strips", q: 2, p: 1 },
      { c: "Supplies", l: "Pen needles", q: 10 },
      { c: "Clothes", l: "Socks", q: 3 },
    ]);
  });

  it("replays what is already packed and plays it in", async () => {
    render(PackingListPage, {});
    await expect.poll(() => live.calls).toEqual(["play"]);
    const { timeline } = JSON.parse(live.scenes[0]!) as { timeline: { events: { op: Op }[] } };
    expect(timeline.events.filter(({ op }) => op === "dry_all")).toHaveLength(1);
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

  it("hands over packs made before the player was ready once it is", async () => {
    live.readyAtMount = false;
    render(PackingListPage, {});
    await expect.poll(() => live.scenes.length).toBe(1);
    await packed("Pen needles").click();
    await expect.element(packed("Pen needles")).toBeChecked();
    expect(live.paints).toEqual([]);

    live.ready();
    expect(live.paints).toHaveLength(1);
    expect(kinds(live.paints[0]!.ops)).toContain("brush");
  });

  it("finishes each paint at once under reduced motion", async () => {
    reducedMotion.current = true;
    render(PackingListPage, {});
    await expect.poll(() => live.calls).toEqual(["finishImmediately"]);
    await packed("Pen needles").click();
    await expect.poll(() => live.calls).toEqual(["finishImmediately", "paint", "finishImmediately"]);
  });

  describe("without a live engine", () => {
    const seeks = () => spyPlayer.seekTo.mock.calls.map(([at]) => at);
    const PAINT_END = 0.2;

    beforeEach(() => {
      live.mode = "none";
    });

    it("seeks the catalogue suitcase's reveal by the share packed, and plays the settle when complete", async () => {
      render(PackingListPage, {});
      await expect.poll(() => seeks().at(-1)).toBeCloseTo(PAINT_END / 3, 10);
      await packed("Pen needles").click();
      await expect.poll(() => seeks().at(-1)).toBeCloseTo((PAINT_END * 2) / 3, 10);
      await packed("Socks").click();
      await expect.poll(() => spyPlayer.play.mock.calls.length).toBe(1);
      expect(seeks().every((at) => at <= PAINT_END)).toBe(true);
      expect(live.paints).toEqual([]);
    });

    it("shows the plain icon when the catalogue suitcase cannot paint either", async () => {
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
