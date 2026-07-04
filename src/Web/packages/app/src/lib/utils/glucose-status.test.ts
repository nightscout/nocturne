import { describe, it, expect } from "vitest";
import { GlucoseStatus } from "$lib/api/generated/nocturne-api-client";
import type { TenantOverviewItem } from "$lib/api/generated/nocturne-api-client";
import {
  glucoseStatusStyles,
  glucoseStatusSortOrder,
  getGlucoseStatusStyle,
  sortTenantsByUrgency,
} from "./glucose-status";

describe("glucoseStatusStyles", () => {
  it("maps every GlucoseStatus value", () => {
    for (const status of Object.values(GlucoseStatus)) {
      expect(glucoseStatusStyles[status]).toBeDefined();
      expect(glucoseStatusStyles[status].text).toMatch(/^text-/);
      expect(glucoseStatusStyles[status].bg).toMatch(/^bg-/);
    }
  });

  it("maps glucose statuses to the glucose color scale", () => {
    expect(glucoseStatusStyles[GlucoseStatus.UrgentLow].text).toBe(
      "text-glucose-very-low"
    );
    expect(glucoseStatusStyles[GlucoseStatus.Low].text).toBe(
      "text-glucose-low"
    );
    expect(glucoseStatusStyles[GlucoseStatus.InRange].text).toBe(
      "text-glucose-in-range"
    );
    expect(glucoseStatusStyles[GlucoseStatus.High].text).toBe(
      "text-glucose-high"
    );
    expect(glucoseStatusStyles[GlucoseStatus.UrgentHigh].text).toBe(
      "text-glucose-very-high"
    );
  });

  it("maps stale and unknown to muted styling", () => {
    expect(glucoseStatusStyles[GlucoseStatus.Stale].text).toBe(
      "text-muted-foreground"
    );
    expect(glucoseStatusStyles[GlucoseStatus.Unknown].text).toBe(
      "text-muted-foreground"
    );
  });
});

describe("glucoseStatusSortOrder", () => {
  it("ranks every GlucoseStatus value", () => {
    for (const status of Object.values(GlucoseStatus)) {
      expect(glucoseStatusSortOrder[status]).toEqual(expect.any(Number));
    }
  });

  it("orders by urgency: UrgentLow, UrgentHigh, Low, High, Stale, InRange, Unknown", () => {
    const ordered = [
      GlucoseStatus.UrgentLow,
      GlucoseStatus.UrgentHigh,
      GlucoseStatus.Low,
      GlucoseStatus.High,
      GlucoseStatus.Stale,
      GlucoseStatus.InRange,
      GlucoseStatus.Unknown,
    ];
    for (let i = 1; i < ordered.length; i++) {
      expect(glucoseStatusSortOrder[ordered[i - 1]]).toBeLessThan(
        glucoseStatusSortOrder[ordered[i]]
      );
    }
  });
});

describe("getGlucoseStatusStyle", () => {
  it("falls back to the Unknown style for an unrecognized status string", () => {
    // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- simulate a server enum value this client build doesn't know
    expect(getGlucoseStatusStyle("SomethingNew" as GlucoseStatus)).toEqual(
      glucoseStatusStyles[GlucoseStatus.Unknown]
    );
  });

  it("falls back to the Unknown style for undefined", () => {
    expect(getGlucoseStatusStyle(undefined)).toEqual(
      glucoseStatusStyles[GlucoseStatus.Unknown]
    );
  });

  it("returns the mapped style for a known status", () => {
    expect(getGlucoseStatusStyle(GlucoseStatus.Low)).toEqual(
      glucoseStatusStyles[GlucoseStatus.Low]
    );
  });
});

describe("sortTenantsByUrgency", () => {
  const tenant = (
    slug: string,
    status: GlucoseStatus | undefined,
    displayName = slug
  ): TenantOverviewItem => ({ slug, displayName, status });

  it("sorts by status urgency, then display name", () => {
    const sorted = sortTenantsByUrgency([
      tenant("zoe", GlucoseStatus.InRange),
      tenant("amy", GlucoseStatus.InRange),
      tenant("bob", GlucoseStatus.UrgentLow),
      tenant("cat", GlucoseStatus.High),
      tenant("dan", GlucoseStatus.Stale),
      tenant("eve", GlucoseStatus.UrgentHigh),
    ]);
    expect(sorted.map((t) => t.slug)).toEqual([
      "bob",
      "eve",
      "cat",
      "dan",
      "amy",
      "zoe",
    ]);
  });

  it("ranks unrecognized and missing statuses like Unknown (last)", () => {
    const sorted = sortTenantsByUrgency([
      // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- simulate a server enum value this client build doesn't know
      tenant("mystery", "SomethingNew" as GlucoseStatus),
      tenant("nobody", undefined),
      tenant("ok", GlucoseStatus.InRange),
    ]);
    expect(sorted[0].slug).toBe("ok");
    expect(sorted.slice(1).map((t) => t.slug)).toEqual(["mystery", "nobody"]);
  });

  it("does not mutate the input array", () => {
    const input = [
      tenant("b", GlucoseStatus.InRange),
      tenant("a", GlucoseStatus.Low),
    ];
    const copy = [...input];
    sortTenantsByUrgency(input);
    expect(input).toEqual(copy);
  });
});
