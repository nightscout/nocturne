import { describe, it, expect } from "vitest";
import {
  UNKNOWN_DIRECTION_GLYPH,
  canonicalDirection,
  deltaColorClass,
  directionArrowCount,
  directionGlyph,
  directionRotation,
} from "@nocturne/ui/glucose";

describe("canonicalDirection", () => {
  it("accepts the casing and separator variants callers hold", () => {
    expect(canonicalDirection("FortyFiveUp")).toBe("FortyFiveUp");
    expect(canonicalDirection("FORTY_FIVE_UP")).toBe("FortyFiveUp");
    expect(canonicalDirection("NONE")).toBe("None");
    expect(canonicalDirection("NOT COMPUTABLE")).toBe("NotComputable");
    expect(canonicalDirection("RATE OUT OF RANGE")).toBe("RateOutOfRange");
    expect(canonicalDirection("CGM ERROR")).toBe("CgmError");
  });

  it("accepts the fastest directions the backend can report", () => {
    expect(canonicalDirection("TripleUp")).toBe("TripleUp");
    expect(canonicalDirection("TRIPLEUP")).toBe("TripleUp");
    expect(canonicalDirection("TripleDown")).toBe("TripleDown");
    expect(canonicalDirection("triple_down")).toBe("TripleDown");
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

  it("draws the tripled directions rather than marking them unknown", () => {
    expect(directionGlyph("TripleUp")).toBe("⤊");
    expect(directionGlyph("TripleDown")).toBe("⤋");
    expect(directionGlyph("TripleUp")).not.toBe(UNKNOWN_DIRECTION_GLYPH);
    expect(directionGlyph("TripleDown")).not.toBe(UNKNOWN_DIRECTION_GLYPH);
    expect(directionGlyph("TripleUp")).not.toBe(directionGlyph("DoubleUp"));
    expect(directionGlyph("TripleDown")).not.toBe(directionGlyph("DoubleDown"));
  });

  it("marks an unknown trend instead of showing the Flat arrow", () => {
    for (const direction of [
      "None",
      "NONE",
      "NotComputable",
      "NOT COMPUTABLE",
      "CgmError",
      "CGM ERROR",
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
    expect(directionRotation("TripleUp")).toBe(0);
    expect(directionRotation("TripleDown")).toBe(180);
  });

  it("refuses a rotation for a direction no arrow can express", () => {
    for (const direction of [
      "None",
      "NotComputable",
      "RateOutOfRange",
      "CgmError",
      "Sideways",
      undefined,
    ]) {
      expect(directionRotation(direction)).toBeNull();
      expect(directionRotation(direction)).not.toBe(directionRotation("Flat"));
    }
  });
});

describe("directionArrowCount", () => {
  it("counts an arrow per step of the rise/fall ramp", () => {
    expect(directionArrowCount("SingleUp")).toBe(1);
    expect(directionArrowCount("FortyFiveDown")).toBe(1);
    expect(directionArrowCount("Flat")).toBe(1);
    expect(directionArrowCount("DoubleUp")).toBe(2);
    expect(directionArrowCount("DoubleDown")).toBe(2);
    expect(directionArrowCount("TripleUp")).toBe(3);
    expect(directionArrowCount("TRIPLE_DOWN")).toBe(3);
  });

  it("draws no arrow for a direction no arrow can express", () => {
    expect(directionArrowCount("None")).toBe(0);
    expect(directionArrowCount("NotComputable")).toBe(0);
    expect(directionArrowCount("RateOutOfRange")).toBe(0);
    expect(directionArrowCount(undefined)).toBe(0);
  });

  it("keeps the tripled directions distinct from the rotation they share", () => {
    expect(directionArrowCount("TripleUp")).not.toBe(
      directionArrowCount("SingleUp")
    );
    expect(directionArrowCount("TripleUp")).not.toBe(
      directionArrowCount("DoubleUp")
    );
    expect(directionArrowCount("TripleDown")).not.toBe(
      directionArrowCount("DoubleDown")
    );
  });
});

describe("deltaColorClass", () => {
  it("classifies the fastest directions as critical", () => {
    const critical = deltaColorClass("DoubleUp");
    expect(deltaColorClass("TripleUp")).toBe(critical);
    expect(deltaColorClass("TripleDown")).toBe(critical);
    expect(deltaColorClass("DoubleDown")).toBe(critical);
    expect(deltaColorClass("SingleUp")).not.toBe(critical);
  });

  it("folds the spelling before classifying, as the stores hand it over unfolded", () => {
    const muted = deltaColorClass("Sideways");
    for (const spelling of ["DOUBLEUP", "double_up", "Double Up"]) {
      expect(deltaColorClass(spelling)).toBe(deltaColorClass("DoubleUp"));
      expect(deltaColorClass(spelling)).not.toBe(muted);
    }
    expect(deltaColorClass("FLAT")).toBe(deltaColorClass("Flat"));
    expect(deltaColorClass("FORTY_FIVE_DOWN")).toBe(
      deltaColorClass("FortyFiveDown")
    );
  });

  it("mutes an absent or unrecognised direction", () => {
    expect(deltaColorClass("")).toBe(deltaColorClass("Sideways"));
    expect(deltaColorClass(undefined)).toBe(deltaColorClass("Sideways"));
    expect(deltaColorClass(null)).toBe(deltaColorClass("Sideways"));
    expect(deltaColorClass("")).not.toBe(deltaColorClass("Flat"));
  });
});
