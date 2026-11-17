import { describe, it, expect } from "vitest";
import { carbAxisMax, carbsOnInsulinScale } from "./carb-axis";

describe("carbAxisMax", () => {
  it("rounds the largest value up to a nice tick", () => {
    expect(carbAxisMax([180, 384, 150])).toBe(400);
    expect(carbAxisMax([46, 84])).toBe(90);
  });

  it("keeps a scale when every value is zero or there are none", () => {
    expect(carbAxisMax([0, 0])).toBe(1);
    expect(carbAxisMax([])).toBe(1);
  });
});

describe("carbsOnInsulinScale", () => {
  it("puts the top of the carbs axis on the top of the insulin axis", () => {
    expect(carbsOnInsulinScale(400, 400, 60)).toBe(60);
    expect(carbsOnInsulinScale(200, 400, 60)).toBe(30);
    expect(carbsOnInsulinScale(0, 400, 60)).toBe(0);
  });

  it("returns zero for an empty axis", () => {
    expect(carbsOnInsulinScale(50, 0, 60)).toBe(0);
  });
});
