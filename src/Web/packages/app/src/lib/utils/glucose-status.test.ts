import { describe, it, expect } from "vitest";
import { GlucoseStatus } from "#lib/api/generated/nocturne-api-client.js";
import {
  glucoseStatusStyles,
  getGlucoseStatusClass,
  getGlucoseTileVariant,
} from "./glucose-status";
import { glucoseTileVariant } from "@nocturne/ui/glucose";

describe("glucoseStatusStyles", () => {
  it("maps every GlucoseStatus value", () => {
    for (const status of Object.values(GlucoseStatus)) {
      expect(glucoseStatusStyles[status]).toBeDefined();
    }
  });
});

describe("getGlucoseStatusClass", () => {
  it("falls back to the Unknown style for an unrecognized status string", () => {
    expect(getGlucoseStatusClass("SomethingNew" as GlucoseStatus)).toEqual(
      glucoseStatusStyles[GlucoseStatus.Unknown]
    );
  });

  it("falls back to the Unknown style for undefined", () => {
    expect(getGlucoseStatusClass(undefined)).toEqual(
      glucoseStatusStyles[GlucoseStatus.Unknown]
    );
  });

  it("returns the mapped style for a known status", () => {
    expect(getGlucoseStatusClass(GlucoseStatus.Low)).toEqual(
      glucoseStatusStyles[GlucoseStatus.Low]
    );
  });
});

describe("getGlucoseTileVariant", () => {
  it.each([
    [GlucoseStatus.UrgentLow, "very-low"],
    [GlucoseStatus.Low, "low"],
    [GlucoseStatus.InRange, "in-range"],
    [GlucoseStatus.High, "high"],
    [GlucoseStatus.UrgentHigh, "very-high"],
    [GlucoseStatus.Stale, "neutral"],
    [GlucoseStatus.Unknown, "neutral"],
  ])("maps %s to %s", (status, variant) => {
    expect(getGlucoseTileVariant(status)).toBe(variant);
  });

  it("is neutral for a missing status or one this build does not know", () => {
    expect(getGlucoseTileVariant(undefined)).toBe("neutral");
    expect(getGlucoseTileVariant("SomethingNew" as GlucoseStatus)).toBe(
      "neutral"
    );
  });
});

describe("glucoseTileVariant", () => {
  it("maps a status string from a surface with no generated client", () => {
    expect(glucoseTileVariant("High")).toBe("high");
  });

  it("is neutral for an unknown string, including an Object prototype key", () => {
    expect(glucoseTileVariant("SomethingNew")).toBe("neutral");
    expect(glucoseTileVariant("toString")).toBe("neutral");
    expect(glucoseTileVariant(null)).toBe("neutral");
  });
});
