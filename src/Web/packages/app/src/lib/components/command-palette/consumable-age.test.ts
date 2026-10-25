import { describe, expect, it } from "vitest";
import { LegacyConsumableKind, type TrackerInstanceDto } from "$api";
import { consumableAgeHours } from "./consumable-age";

const NOW = Date.UTC(2026, 9, 25, 12, 0, 0);
const hoursAgo = (hours: number) => new Date(NOW - hours * 3600_000).toISOString();
const run = (id: string, definitionId: string, hours: number): TrackerInstanceDto => ({
  id,
  definitionId,
  startedAt: hoursAgo(hours),
  ageHours: 999,
});

const { Site, Sensor } = LegacyConsumableKind;

describe("consumableAgeHours", () => {
  it("is the uncovered kind's fallback age at now", () => {
    const fallbacks = [{ kind: Site, latestEventAt: hoursAgo(50), ageHours: 1 }];

    expect(consumableAgeHours(Site, fallbacks, [], [], NOW)).toBe(50);
  });

  it("is the covering tracker's run age at now", () => {
    const coverage = [{ kind: Sensor, definitionIds: ["dexcom"] }];

    expect(consumableAgeHours(Sensor, [], coverage, [run("a", "dexcom", 30)], NOW)).toBe(30);
  });

  it("is undefined for a covered kind with no covering run", () => {
    const coverage = [{ kind: Sensor, definitionIds: ["dexcom"] }];

    expect(consumableAgeHours(Sensor, [], coverage, [run("a", "other", 3)], NOW)).toBeUndefined();
  });

  it("takes the most recently started run of two covering trackers", () => {
    const coverage = [{ kind: Site, definitionIds: ["pod", "set"] }];
    const instances = [run("a", "pod", 60), run("b", "set", 4), run("c", "pod", 20)];

    expect(consumableAgeHours(Site, [], coverage, instances, NOW)).toBe(4);
  });

  it("is undefined for a kind neither uncovered nor covered", () => {
    expect(consumableAgeHours(Site, [], [], [run("a", "pod", 4)], NOW)).toBeUndefined();
  });
});
