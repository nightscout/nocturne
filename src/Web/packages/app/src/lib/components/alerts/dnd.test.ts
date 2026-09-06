import { describe, it, expect } from "vitest";
import type { TenantAlertSettingsResponse } from "$api-clients";
import { isDndActiveNow, isDndScheduleConfigured } from "./dnd";

const settings = (
  overrides: TenantAlertSettingsResponse
): TenantAlertSettingsResponse => ({
  dndManualActive: false,
  dndScheduleEnabled: false,
  ...overrides,
});

describe("isDndActiveNow", () => {
  it("is true only for a manual mute", () => {
    expect(isDndActiveNow(settings({ dndManualActive: true }))).toBe(true);
    expect(isDndActiveNow(settings({}))).toBe(false);
  });

  it("is false when a schedule is merely configured", () => {
    // 22:00-07:00 quiet hours must not read as "on" at noon; the backend
    // evaluates the window and the response has no "active now" field.
    expect(
      isDndActiveNow(
        settings({
          dndScheduleEnabled: true,
          dndScheduleStart: "22:00",
          dndScheduleEnd: "07:00",
        })
      )
    ).toBe(false);
  });

  it("is false for a missing response", () => {
    expect(isDndActiveNow(null)).toBe(false);
    expect(isDndActiveNow(undefined)).toBe(false);
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
