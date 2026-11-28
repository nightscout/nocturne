import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { REVEAL_BUDGET_MS, Reveal } from "./reveal.js";

describe("Reveal", () => {
  let frames: FrameRequestCallback[];
  let now: number;

  beforeEach(() => {
    frames = [];
    now = 0;
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => frames.push(callback));
    vi.stubGlobal("cancelAnimationFrame", () => {});
    vi.spyOn(performance, "now").mockImplementation(() => now);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  function nextFrame() {
    const frame = frames.shift();
    expect(frame).toBeDefined();
    frame?.(now);
  }

  it("runs the steps after one that throws in the next frame", () => {
    const reveal = new Reveal();
    const ran: string[] = [];
    reveal.schedule(() => {
      throw new Error("a row failed to mount");
    });
    reveal.schedule(() => ran.push("next"));

    expect(nextFrame).toThrow("a row failed to mount");
    nextFrame();

    expect(ran).toEqual(["next"]);
    expect(frames).toHaveLength(0);
  });

  it("asks for a frame again once a step that threw has emptied the queue", () => {
    const reveal = new Reveal();
    reveal.schedule(() => {
      throw new Error("a row failed to mount");
    });
    expect(nextFrame).toThrow();

    const ran: string[] = [];
    reveal.schedule(() => ran.push("later"));
    nextFrame();

    expect(ran).toEqual(["later"]);
  });

  it("leaves the steps past the budget for the next frame", () => {
    const reveal = new Reveal();
    const ran: number[] = [];
    for (let step = 0; step < 3; step++) {
      reveal.schedule(() => {
        ran.push(step);
        now += REVEAL_BUDGET_MS / 2;
      });
    }

    nextFrame();
    expect(ran).toEqual([0, 1]);
    nextFrame();
    expect(ran).toEqual([0, 1, 2]);
  });

  it("sizes a step to the budget left in the frame at the pace of the last one", () => {
    const reveal = new Reveal();
    const perFrame: number[][] = [];
    const msPerUnit = [2, 0.5, REVEAL_BUDGET_MS / 4];
    const take = (count: number) => {
      perFrame.at(-1)?.push(count);
      const taken = perFrame.flat().length;
      now += count * (msPerUnit[taken - 1] ?? 0);
      if (taken < 4) reveal.scheduleSized(10, take);
    };
    reveal.scheduleSized(10, take);

    while (frames.length > 0) {
      perFrame.push([]);
      nextFrame();
    }

    // 10 units at 2 ms leave 10 ms, room for only 5, so the next step waits for a frame of its own. A frame that starts
    // at 7.5 ms a unit takes 4, the most that fit in the whole budget.
    expect(perFrame).toEqual([[10], [10, 10], [4]]);
  });

  it("keeps one sized step waiting and drops a second call while it waits", () => {
    const reveal = new Reveal();
    const counts: number[] = [];
    reveal.scheduleSized(10, (count) => counts.push(count));
    reveal.scheduleSized(10, (count) => counts.push(-count));

    while (frames.length > 0) nextFrame();

    expect(counts).toEqual([10]);
  });

  it("advances from the count a poll set while the step waited, never back to the count it was asked from", () => {
    const reveal = new Reveal();
    let rows = 10;
    const seen: number[] = [];
    const advance = () =>
      reveal.scheduleSized(10, (count) => {
        rows += count;
        seen.push(rows);
        if (rows < 60) advance();
      });

    advance();
    rows = 40;
    advance();
    while (frames.length > 0) nextFrame();

    expect(seen).toEqual([50, 60]);
  });
});
