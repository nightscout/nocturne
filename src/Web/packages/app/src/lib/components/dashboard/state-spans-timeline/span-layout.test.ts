import { describe, expect, it } from "vitest";
import { layoutTrack } from "./span-layout";

const t = (minutes: number) => new Date(Date.UTC(2026, 0, 1, 0, minutes));
// One pixel per minute.
const xScale = (d: Date) => (d.getTime() - t(0).getTime()) / 60_000;
const range = { from: t(0), to: t(600) };

describe("layoutTrack", () => {
  it("leaves spans wide enough alone", () => {
    const laid = layoutTrack([{ startTime: t(0), endTime: t(60) }, { startTime: t(60), endTime: t(200) }], xScale, range);
    expect(laid.map((l) => [l.x, l.width])).toEqual([[0, 60], [60, 140]]);
  });

  it("widens a sub-pixel span to the floor and takes the pixels from the next span", () => {
    const laid = layoutTrack(
      [
        { startTime: t(0), endTime: t(60) },
        { startTime: t(60), endTime: t(61) },
        { startTime: t(61), endTime: t(200) },
      ],
      xScale,
      range,
    );
    expect(laid.map((l) => [l.x, l.width])).toEqual([[0, 60], [60, 4], [64, 136]]);
  });

  it("lets a cluster of floor-width spans borrow from the first neighbour with room, keeping the track length", () => {
    const laid = layoutTrack(
      [
        { startTime: t(0), endTime: t(1) },
        { startTime: t(1), endTime: t(2) },
        { startTime: t(2), endTime: t(3) },
        { startTime: t(3), endTime: t(100) },
      ],
      xScale,
      range,
    );
    expect(laid.map((l) => [l.x, l.width])).toEqual([[0, 4], [4, 4], [8, 4], [12, 88]]);
  });

  it("borrows from the left neighbour when the right one is at the floor", () => {
    const laid = layoutTrack(
      [
        { startTime: t(0), endTime: t(100) },
        { startTime: t(100), endTime: t(101) },
        { startTime: t(101), endTime: t(105) },
      ],
      xScale,
      range,
    );
    expect(laid.map((l) => [l.x, l.width])).toEqual([[0, 97], [97, 4], [101, 4]]);
  });

  it("never moves spans beyond the plot edge when tiny spans accumulate", () => {
    const spans = Array.from({ length: 50 }, (_, i) => ({ startTime: t(i * 10), endTime: t(i * 10 + 1) }));
    spans.push({ startTime: t(499), endTime: t(600) });
    const laid = layoutTrack(spans, xScale, range);
    expect(Math.max(...laid.map((l) => l.x + l.width))).toBe(600);
    expect(laid.every((l) => l.width >= 4)).toBe(true);
  });

  it("does not close a real gap between spans", () => {
    const laid = layoutTrack([{ startTime: t(0), endTime: t(1) }, { startTime: t(50), endTime: t(80) }], xScale, range);
    expect(laid.map((l) => [l.x, l.width])).toEqual([[0, 4], [50, 30]]);
  });

  it("clips a span that ends after the range and drops one entirely outside it", () => {
    const laid = layoutTrack(
      [
        { startTime: t(-100), endTime: t(-10) },
        { startTime: t(-10), endTime: t(50) },
        { startTime: t(550), endTime: t(900) },
      ],
      xScale,
      range,
    );
    expect(laid.map((l) => [l.x, l.width])).toEqual([[0, 50], [550, 50]]);
  });

  it("runs an open span to the end of the range and orders by start time", () => {
    const laid = layoutTrack([{ startTime: t(500), endTime: null }, { startTime: t(0), endTime: t(500) }], xScale, range);
    expect(laid.map((l) => [l.x, l.width])).toEqual([[0, 500], [500, 100]]);
  });
});
