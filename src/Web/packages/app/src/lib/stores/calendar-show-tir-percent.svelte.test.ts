import type {} from "@vitest/browser-playwright";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { UserDisplayPreferences } from "#lib/api/index.js";
import {
  calendarShowTirPercent,
  collectPreferences,
  PREFERENCES_WRITE_THROUGH_DELAY_MS,
  registerPreferencesWriteThrough,
} from "./appearance-store.svelte";

describe("calendar show TIR percent preference", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    calendarShowTirPercent.hydrate(false);
  });

  it("is off until the user turns it on", () => {
    expect(calendarShowTirPercent.current).toBe(false);
  });

  it("saves to the device and to the user's backend preferences when set", () => {
    const saved = vi.fn<(prefs: UserDisplayPreferences) => unknown>();
    registerPreferencesWriteThrough(saved);

    calendarShowTirPercent.current = true;

    expect(localStorage.getItem("nocturne-calendar-show-tir-percent")).toBe("true");
    expect(collectPreferences().calendarShowTirPercent).toBe(true);
    vi.advanceTimersByTime(PREFERENCES_WRITE_THROUGH_DELAY_MS);
    expect(saved).toHaveBeenCalledOnce();
    expect(saved.mock.lastCall?.[0].calendarShowTirPercent).toBe(true);
  });

  it("stops saving to the backend once the write-through is withdrawn", () => {
    const saved = vi.fn<(prefs: UserDisplayPreferences) => unknown>();
    registerPreferencesWriteThrough(saved);
    registerPreferencesWriteThrough(null);

    calendarShowTirPercent.current = true;

    expect(collectPreferences().calendarShowTirPercent).toBe(true);
    vi.advanceTimersByTime(PREFERENCES_WRITE_THROUGH_DELAY_MS * 2);
    expect(saved).not.toHaveBeenCalled();
  });
});
