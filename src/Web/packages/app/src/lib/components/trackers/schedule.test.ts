import { describe, expect, it } from "vitest";
import {
  LegacyConsumableKind,
  NotificationUrgency,
  type LegacyFallbackDto,
  type TrackerInstanceDto,
} from "#lib/api/index.js";
import { reachedStep, reachedUrgency } from "./schedule";

const HOUR = 60 * 60 * 1000;
const start = Date.parse("2026-09-01T00:00:00Z");
const at = (hours: number) => new Date(start + hours * HOUR).toISOString();

function run(steps: [NotificationUrgency, number, string?][]): TrackerInstanceDto {
  return {
    startedAt: at(0),
    schedule: steps.map(([urgency, hours, description]) => ({
      urgency,
      firesAt: at(hours),
      description,
    })),
  } as TrackerInstanceDto;
}

describe("reachedUrgency", () => {
  it("is null before the first step fires", () => {
    const instance = run([[NotificationUrgency.Info, 216]]);
    expect(reachedUrgency(instance, start + 1 * HOUR)).toBeNull();
  });

  it("does not treat a before-the-end step as fired from the start", () => {
    // A ten-day tracker's "24h before end" step is resolved to hour 216 by the server; the
    // regression this pins lit the pill at hour 0 by comparing the age against -24.
    const instance = run([[NotificationUrgency.Warn, 216]]);
    expect(reachedUrgency(instance, start)).toBeNull();
    expect(reachedUrgency(instance, start + 216 * HOUR)).toBe(NotificationUrgency.Warn);
  });

  it("keeps the most urgent step reached, whatever the order they fire in", () => {
    const instance = run([
      [NotificationUrgency.Urgent, 10],
      [NotificationUrgency.Info, 20],
    ]);
    expect(reachedUrgency(instance, start + 30 * HOUR)).toBe(NotificationUrgency.Urgent);
  });

  it("is null for a run with no schedule", () => {
    expect(reachedUrgency({} as TrackerInstanceDto, start)).toBeNull();
  });
});

describe("reachedStep", () => {
  it("returns the reached step's message", () => {
    const instance = run([
      [NotificationUrgency.Info, 1, "Order a replacement"],
      [NotificationUrgency.Warn, 5, "Change soon"],
    ]);
    expect(reachedStep(instance, start + 6 * HOUR)?.description).toBe("Change soon");
  });
});

describe("reachedUrgency for a legacy fallback", () => {
  it("reads a fallback's schedule as it reads a running tracker's", () => {
    const fallback: LegacyFallbackDto = {
      kind: LegacyConsumableKind.Site,
      ageHours: 50,
      schedule: [
        { urgency: NotificationUrgency.Info, firesAt: at(44) },
        { urgency: NotificationUrgency.Warn, firesAt: at(48) },
        { urgency: NotificationUrgency.Urgent, firesAt: at(72) },
      ],
    };
    expect(reachedUrgency(fallback, start + 50 * HOUR)).toBe(NotificationUrgency.Warn);
    expect(reachedUrgency(fallback, start + 10 * HOUR)).toBeNull();
  });
});
