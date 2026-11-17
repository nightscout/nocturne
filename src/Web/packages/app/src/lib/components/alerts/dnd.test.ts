import { describe, it, expect } from "vitest";
import type { TenantAlertSettingsResponse } from "#lib/api/generated/nocturne-api-client.js";
import {
  isDndInEffect,
  isDndManualActive,
  isDndScheduleActiveNow,
  isDndScheduleConfigured,
} from "./dnd";

const settings = (
  overrides: TenantAlertSettingsResponse
): TenantAlertSettingsResponse => ({
  dndManualActive: false,
  dndScheduleEnabled: false,
  dndScheduleActiveNow: false,
  ...overrides,
});

describe("isDndInEffect", () => {
  it("is true for a manual mute", () => {
    expect(isDndInEffect(settings({ dndManualActive: true }))).toBe(true);
  });

  it("is true while the schedule is in effect", () => {
    expect(
      isDndInEffect(
        settings({ dndScheduleEnabled: true, dndScheduleActiveNow: true })
      )
    ).toBe(true);
  });

  it("is false when a schedule is configured but not in effect", () => {
    expect(
      isDndInEffect(
        settings({
          dndScheduleEnabled: true,
          dndScheduleStart: "22:00",
          dndScheduleEnd: "07:00",
        })
      )
    ).toBe(false);
  });

  it("is false for a missing response", () => {
    expect(isDndInEffect(null)).toBe(false);
    expect(isDndInEffect(undefined)).toBe(false);
  });
});

describe("isDndManualActive", () => {
  it("ignores the schedule", () => {
    expect(
      isDndManualActive(
        settings({ dndScheduleEnabled: true, dndScheduleActiveNow: true })
      )
    ).toBe(false);
    expect(isDndManualActive(settings({ dndManualActive: true }))).toBe(true);
  });
});

describe("isDndScheduleActiveNow", () => {
  it("reads the backend's verdict", () => {
    expect(
      isDndScheduleActiveNow(settings({ dndScheduleActiveNow: true }))
    ).toBe(true);
    expect(isDndScheduleActiveNow(settings({}))).toBe(false);
    expect(isDndScheduleActiveNow(null)).toBe(false);
  });
});

describe("isDndScheduleConfigured", () => {
  it("reflects the configured flag, not whether it is in effect", () => {
    expect(
      isDndScheduleConfigured(settings({ dndScheduleEnabled: true }))
    ).toBe(true);
    expect(isDndScheduleConfigured(settings({}))).toBe(false);
    expect(isDndScheduleConfigured(null)).toBe(false);
  });
});
