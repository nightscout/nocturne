import { describe, it, expect } from "vitest";
import type { SensorGlucose, GlucoseCluster } from "$lib/api";
import { buildDayBuckets } from "./buckets";

const entry = (
  mills: number,
  mgdl = 100,
  utcOffset?: number
): SensorGlucose => ({ mills, mgdl, utcOffset });

const iso = (value: string) => Date.parse(value);

describe("buildDayBuckets", () => {
  it("buckets on the patient zone when the first reading has no utcOffset", () => {
    // 2026-09-10T15:00:00Z is 2026-09-11 01:00 in Sydney (AEST, +10).
    const buckets = buildDayBuckets(
      [entry(iso("2026-09-10T15:00:00Z"))],
      [],
      [],
      ["2026-09-10", "2026-09-11"],
      "Australia/Sydney"
    );

    expect(buckets).toHaveLength(2);
    expect(buckets[0].dateMs).toBe(iso("2026-09-09T14:00:00Z"));
    expect(buckets[0].points).toHaveLength(0);
    expect(buckets[1].dateMs).toBe(iso("2026-09-10T14:00:00Z"));
    expect(buckets[1].points).toEqual([{ x: 60, y: 100 }]);
    expect(buckets[1].label).toContain("11");
  });

  it("keeps a 23:30 local reading on that local day, not the next", () => {
    // 2026-09-11T03:30:00Z is 2026-09-10 23:30 in Toronto (EDT, -4).
    const buckets = buildDayBuckets(
      [entry(iso("2026-09-11T03:30:00Z"), 100, 0)],
      [],
      [],
      ["2026-09-10", "2026-09-11"],
      "America/Toronto"
    );

    expect(buckets[0].points).toEqual([{ x: 1410, y: 100 }]);
    expect(buckets[1].points).toHaveLength(0);
  });

  it("measures x as elapsed minutes from the day's own midnight across a spring-forward day", () => {
    // Toronto jumps 2026-03-08 02:00 EST -> 03:00 EDT, so 03:30 local is 150
    // elapsed minutes past that day's midnight.
    const buckets = buildDayBuckets(
      [entry(iso("2026-03-08T06:30:00Z")), entry(iso("2026-03-08T07:30:00Z"))],
      [],
      [],
      ["2026-03-08"],
      "America/Toronto"
    );

    expect(buckets).toHaveLength(1);
    expect(buckets[0].dateMs).toBe(iso("2026-03-08T05:00:00Z"));
    expect(buckets[0].points.map((p) => p.x)).toEqual([90, 150]);
  });

  it("splits a cluster across local midnight, counting it once on its start day", () => {
    const cluster: GlucoseCluster = {
      start: "2026-09-10T13:30:00Z",
      end: "2026-09-10T15:30:00Z",
    };
    // In Sydney the cluster spans 23:30 Sep 10 to 01:30 Sep 11.
    const buckets = buildDayBuckets(
      [],
      [cluster],
      [],
      ["2026-09-10", "2026-09-11"],
      "Australia/Sydney"
    );

    expect(buckets[0].bands).toEqual([{ xStart: 1410, xEnd: 1440, cluster }]);
    expect(buckets[1].bands).toEqual([{ xStart: 0, xEnd: 90, cluster }]);
    expect(buckets[0].clusterCount).toBe(1);
    expect(buckets[1].clusterCount).toBe(0);
  });
});
