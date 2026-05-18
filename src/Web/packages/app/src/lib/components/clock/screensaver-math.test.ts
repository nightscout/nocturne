import { describe, it, expect } from "vitest";
import {
  advance,
  reflect,
  computeAngleToCorner,
  type Vec2,
  type Bounds,
} from "./screensaver-math";

const bounds = (w: number, h: number): Bounds => ({
  blockW: 100,
  blockH: 50,
  viewportW: w,
  viewportH: h,
});

describe("advance", () => {
  it("moves position by velocity * dt", () => {
    const next = advance(
      { x: 10, y: 20 },
      { x: 60, y: 0 },
      bounds(1000, 1000),
      1 // 1 second
    );
    expect(next.pos).toEqual({ x: 70, y: 20 });
    expect(next.vel).toEqual({ x: 60, y: 0 });
    expect(next.hitLeft).toBe(false);
    expect(next.hitRight).toBe(false);
    expect(next.hitTop).toBe(false);
    expect(next.hitBottom).toBe(false);
  });

  it("reflects off right wall and clamps inside bounds", () => {
    const next = advance(
      { x: 880, y: 20 },
      { x: 60, y: 0 },
      bounds(1000, 1000),
      1
    );
    expect(next.pos.x).toBe(900);
    expect(next.vel.x).toBeLessThan(0);
    expect(next.hitRight).toBe(true);
  });

  it("reflects off left wall", () => {
    const next = advance(
      { x: 20, y: 100 },
      { x: -60, y: 0 },
      bounds(1000, 1000),
      1
    );
    expect(next.pos.x).toBe(0);
    expect(next.vel.x).toBeGreaterThan(0);
    expect(next.hitLeft).toBe(true);
  });

  it("reflects off top wall", () => {
    const next = advance(
      { x: 100, y: 10 },
      { x: 0, y: -30 },
      bounds(1000, 1000),
      1
    );
    expect(next.pos.y).toBe(0);
    expect(next.vel.y).toBeGreaterThan(0);
    expect(next.hitTop).toBe(true);
  });

  it("reflects off bottom wall", () => {
    const next = advance(
      { x: 100, y: 940 },
      { x: 0, y: 30 },
      bounds(1000, 1000),
      1
    );
    expect(next.pos.y).toBe(950);
    expect(next.vel.y).toBeLessThan(0);
    expect(next.hitBottom).toBe(true);
  });

  it("detects a corner hit when both axes clamp in the same frame", () => {
    const next = advance(
      { x: 880, y: 940 },
      { x: 60, y: 30 },
      bounds(1000, 1000),
      1
    );
    expect(next.pos).toEqual({ x: 900, y: 950 });
    expect(next.hitRight).toBe(true);
    expect(next.hitBottom).toBe(true);
  });
});

describe("computeAngleToCorner", () => {
  it("produces a velocity that lands on the target corner from a wall start", () => {
    const speed = 60;
    const vel = computeAngleToCorner(
      { x: 0, y: 300 },
      { x: 900, y: 950 },
      speed
    );

    expect(vel.x).toBeGreaterThan(0);
    expect(vel.y).toBeGreaterThan(0);

    const mag = Math.hypot(vel.x, vel.y);
    expect(mag).toBeCloseTo(speed, 5);

    const dx = 900 - 0;
    const dy = 950 - 300;
    const t = Math.hypot(dx, dy) / speed;
    const arrival = { x: 0 + vel.x * t, y: 300 + vel.y * t };
    expect(arrival.x).toBeCloseTo(900, 3);
    expect(arrival.y).toBeCloseTo(950, 3);
  });
});

describe("reflect", () => {
  it("flips x for vertical walls", () => {
    expect(reflect({ x: 5, y: 3 }, "x")).toEqual({ x: -5, y: 3 });
  });
  it("flips y for horizontal walls", () => {
    expect(reflect({ x: 5, y: 3 }, "y")).toEqual({ x: 5, y: -3 });
  });
});
