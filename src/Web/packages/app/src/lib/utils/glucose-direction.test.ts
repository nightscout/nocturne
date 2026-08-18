import { describe, it, expect } from "vitest";
import {
  UNKNOWN_DIRECTION_GLYPH,
  canonicalDirection,
  directionGlyph,
  directionRotation,
  isDoubleArrow,
} from "@nocturne/ui/glucose";

describe("canonicalDirection", () => {
  it("accepts the casing and separator variants callers hold", () => {
    expect(canonicalDirection("FortyFiveUp")).toBe("FortyFiveUp");
    expect(canonicalDirection("FORTY_FIVE_UP")).toBe("FortyFiveUp");
    expect(canonicalDirection("NONE")).toBe("None");
    expect(canonicalDirection("NOT COMPUTABLE")).toBe("NotComputable");
    expect(canonicalDirection("RATE OUT OF RANGE")).toBe("RateOutOfRange");
  });

  it("returns nothing for an absent or unrecognised direction", () => {
    expect(canonicalDirection(undefined)).toBe("");
    expect(canonicalDirection(null)).toBe("");
    expect(canonicalDirection("")).toBe("");
    expect(canonicalDirection("Sideways")).toBe("");
  });
});

describe("directionGlyph", () => {
  it("maps drawable directions to their arrows", () => {
    expect(directionGlyph("Flat")).toBe("→");
    expect(directionGlyph("SingleUp")).toBe("↑");
    expect(directionGlyph("SingleDown")).toBe("↓");
    expect(directionGlyph("DoubleUp")).toBe("⇈");
    expect(directionGlyph("DoubleDown")).toBe("⇊");
    expect(directionGlyph("FortyFiveUp")).toBe("↗");
    expect(directionGlyph("FortyFiveDown")).toBe("↘");
    expect(directionGlyph("RateOutOfRange")).toBe("⇕");
  });

  it("marks an unknown trend instead of showing the Flat arrow", () => {
    for (const direction of [
      "None",
      "NONE",
      "NotComputable",
      "NOT COMPUTABLE",
    ]) {
      expect(directionGlyph(direction)).toBe(UNKNOWN_DIRECTION_GLYPH);
      expect(directionGlyph(direction)).not.toBe(directionGlyph("Flat"));
    }
  });

  it("marks an absent or unrecognised direction", () => {
    expect(directionGlyph(undefined)).toBe(UNKNOWN_DIRECTION_GLYPH);
    expect(directionGlyph("Sideways")).toBe(UNKNOWN_DIRECTION_GLYPH);
  });
});

describe("directionRotation", () => {
  it("rotates an upward arrow onto each drawable direction", () => {
    expect(directionRotation("DoubleUp")).toBe(0);
    expect(directionRotation("SingleUp")).toBe(0);
    expect(directionRotation("FortyFiveUp")).toBe(45);
    expect(directionRotation("Flat")).toBe(90);
    expect(directionRotation("FortyFiveDown")).toBe(135);
    expect(directionRotation("SingleDown")).toBe(180);
    expect(directionRotation("DoubleDown")).toBe(180);
  });

  it("refuses a rotation for a direction no arrow can express", () => {
    for (const direction of [
      "None",
      "NotComputable",
      "RateOutOfRange",
      "Sideways",
      undefined,
    ]) {
      expect(directionRotation(direction)).toBeNull();
      expect(directionRotation(direction)).not.toBe(directionRotation("Flat"));
    }
  });
});

describe("isDoubleArrow", () => {
  it("is true only for the doubled directions", () => {
    expect(isDoubleArrow("DoubleUp")).toBe(true);
    expect(isDoubleArrow("DoubleDown")).toBe(true);
    expect(isDoubleArrow("SingleUp")).toBe(false);
    expect(isDoubleArrow("None")).toBe(false);
    expect(isDoubleArrow(undefined)).toBe(false);
  });
});
