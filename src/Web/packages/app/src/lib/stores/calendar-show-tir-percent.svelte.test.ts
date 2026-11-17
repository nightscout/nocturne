import type {} from "@vitest/browser-playwright";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { UserDisplayPreferences } from "#lib/api/index.js";
import {
  calendarShowTirPercent,
  collectPreferences,
  registerPreferencesWriteThrough,
} from "./appearance-store.svelte";

describe("calendar show TIR percent preference", () => {
  afterEach(() => {
    calendarShowTirPercent.hydrate(false);
  });

  it("is off until the user turns it on", () => {
    expect(calendarShowTirPercent.current).toBe(false);
  });

  it("saves to the device and to the user's backend preferences when set", async () => {
    const saved = vi.fn<(prefs: UserDisplayPreferences) => unknown>();
    registerPreferencesWriteThrough(saved);

    calendarShowTirPercent.current = true;

    expect(localStorage.getItem("nocturne-calendar-show-tir-percent")).toBe("true");
    expect(collectPreferences().calendarShowTirPercent).toBe(true);
    await vi.waitFor(() => expect(saved).toHaveBeenCalled());
    expect(saved.mock.lastCall?.[0].calendarShowTirPercent).toBe(true);
  });

  it("stops saving to the backend once the write-through is withdrawn", async () => {
    const saved = vi.fn<(prefs: UserDisplayPreferences) => unknown>();
    registerPreferencesWriteThrough(saved);
    registerPreferencesWriteThrough(null);

    calendarShowTirPercent.current = true;

    expect(collectPreferences().calendarShowTirPercent).toBe(true);
    await new Promise((resolve) => setTimeout(resolve, 600));
    expect(saved).not.toHaveBeenCalled();
  });
});
