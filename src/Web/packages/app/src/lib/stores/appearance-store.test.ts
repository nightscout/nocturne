import { describe, it, expect, vi, beforeEach } from "vitest";
import type { UserDisplayPreferences } from "$lib/api";

// Node env (browser=false): DOM/cookie I/O is skipped, so we exercise the pure
// hydration mapping — the crux of the cross-device fix (server prefs -> store).
vi.mock("$app/environment", () => ({ browser: false, dev: false }));
vi.mock("$app/navigation", () => ({}));
vi.mock("$app/state", () => ({}));
vi.mock("mode-watcher", () => ({
  setMode: vi.fn(),
  mode: { current: "light" },
  userPrefersMode: { current: "system" },
}));

const {
  glucoseUnits,
  timeFormat,
  colorTheme,
  predictionMinutes,
  predictionEnabled,
  chartLineColor,
  glucoseChartLookback,
  applyPreferences,
  collectPreferences,
  hasStoredPreferences,
  preferenceCookieWrites,
  readCookieFrom,
  resolveInitialLanguage,
} = await import("./appearance-store.svelte");

describe("appearance-store preference sync", () => {
  beforeEach(() => {
    // Reset to defaults between tests (module-level singletons).
    applyPreferences({
      glucoseUnits: "mg/dl",
      timeFormat: "12",
      colorTheme: "nocturne",
      prediction: { enabled: true, minutes: 30 },
      chart: { lineColor: "#22c55e", lookback: 12 },
    });
  });

  it("collectPreferences reflects the current store values", () => {
    const prefs = collectPreferences();
    expect(prefs.glucoseUnits).toBe("mg/dl");
    expect(prefs.timeFormat).toBe("12");
    expect(prefs.colorTheme).toBe("nocturne");
    expect(prefs.prediction?.minutes).toBe(30);
    expect(prefs.chart?.lineColor).toBe("#22c55e");
    expect(prefs.chart?.lookback).toBe(12);
  });

  it("applyPreferences hydrates the store from a server payload (new-device case)", () => {
    applyPreferences({
      glucoseUnits: "mmol",
      timeFormat: "24",
      colorTheme: "trio",
      prediction: { minutes: 45 },
      chart: { lineColor: "#abcdef", lookback: 6 },
    });

    expect(glucoseUnits.current).toBe("mmol");
    expect(timeFormat.current).toBe("24");
    expect(colorTheme.current).toBe("trio");
    expect(predictionMinutes.current).toBe(45);
    expect(chartLineColor.current).toBe("#abcdef");
    expect(glucoseChartLookback.current).toBe(6);
  });

  it("applyPreferences leaves unset fields untouched (partial payload)", () => {
    applyPreferences({ glucoseUnits: "mmol" });

    expect(glucoseUnits.current).toBe("mmol"); // applied
    expect(timeFormat.current).toBe("12"); // untouched
    expect(predictionEnabled.current).toBe(true); // untouched
  });

  it("applyPreferences ignores null/undefined input", () => {
    applyPreferences(null);
    applyPreferences(undefined);
    expect(glucoseUnits.current).toBe("mg/dl");
  });

  it("collectPreferences round-trips through applyPreferences", () => {
    const source: UserDisplayPreferences = {
      glucoseUnits: "mmol",
      timeFormat: "24",
      colorTheme: "aaps",
      nightModeSchedule: true,
      prediction: { enabled: false, minutes: 60, displayMode: "lines" },
      chart: { lineColor: "#000000", pointColor: "#ffffff", showPoints: false, lookback: 4 },
    };

    applyPreferences(source);
    const collected = collectPreferences();

    expect(collected.glucoseUnits).toBe("mmol");
    expect(collected.colorTheme).toBe("aaps");
    expect(collected.nightModeSchedule).toBe(true);
    expect(collected.prediction?.displayMode).toBe("lines");
    expect(collected.chart?.showPoints).toBe(false);
  });

  describe("hasStoredPreferences", () => {
    it("is false for null/empty payloads", () => {
      expect(hasStoredPreferences(null)).toBe(false);
      expect(hasStoredPreferences(undefined)).toBe(false);
      expect(hasStoredPreferences({})).toBe(false);
    });

    it("is true when any value is present", () => {
      expect(hasStoredPreferences({ glucoseUnits: "mmol" })).toBe(true);
      expect(hasStoredPreferences({ prediction: { minutes: 30 } })).toBe(true);
    });
  });
});

describe("preferenceCookieWrites", () => {
  it("writes one host-scoped cookie when there is no domain to widen to", () => {
    expect(preferenceCookieWrites("nocturne-prefs", "v", 60, null)).toEqual([
      "nocturne-prefs=v;path=/;max-age=60;SameSite=Lax",
    ]);
  });

  it("scopes the cookie to the base domain so it crosses tenant subdomains", () => {
    expect(preferenceCookieWrites("nocturne-prefs", "v", 60, ".example.com")).toEqual([
      "nocturne-prefs=;path=/;max-age=0;SameSite=Lax",
      "nocturne-prefs=v;path=/;max-age=60;SameSite=Lax;domain=.example.com",
    ]);
  });

  it("expires the host-scoped cookie before writing the widened one", () => {
    const [first, second] = preferenceCookieWrites("nocturne-language", "en", 60, ".example.com");

    expect(first).not.toContain("domain=");
    expect(first).toContain("max-age=0");
    expect(second).toContain("domain=.example.com");
    expect(second).toContain("max-age=60");
  });

  it("never expires the cookie it is about to write when host-scoped", () => {
    const writes = preferenceCookieWrites("nocturne-language", "en", 60, null);

    expect(writes).toHaveLength(1);
    expect(writes[0]).not.toContain("max-age=0");
  });

  it("carries the name, value and lifetime it was given", () => {
    expect(preferenceCookieWrites("nocturne-language", "fr", 31536000, null)).toEqual([
      "nocturne-language=fr;path=/;max-age=31536000;SameSite=Lax",
    ]);
  });
});

describe("readCookieFrom", () => {
  it("reads a cookie out of the header", () => {
    expect(readCookieFrom("a=1; nocturne-prefs=x; b=2", "nocturne-prefs")).toBe("x");
  });

  it("takes the newer of two same-name cookies, not the first", () => {
    expect(readCookieFrom("nocturne-prefs=stale; nocturne-prefs=fresh", "nocturne-prefs"))
      .toBe("fresh");
  });

  it("does not match a cookie whose name merely starts the same", () => {
    expect(readCookieFrom("nocturne-prefs-old=x", "nocturne-prefs")).toBeNull();
  });

  it("reads an empty value as empty rather than absent", () => {
    expect(readCookieFrom("nocturne-prefs=", "nocturne-prefs")).toBe("");
  });

  it("finds nothing in an empty or unrelated header", () => {
    expect(readCookieFrom("", "nocturne-prefs")).toBeNull();
    expect(readCookieFrom("other=1", "nocturne-prefs")).toBeNull();
  });
});

describe("resolveInitialLanguage", () => {
  it("adopts the shared cookie when this origin stored nothing", () => {
    expect(resolveInitialLanguage(null, "de")).toBe("de");
  });

  it("keeps this origin's stored language over the cookie", () => {
    expect(resolveInitialLanguage(JSON.stringify("fr"), "de")).toBeNull();
  });

  it("keeps a stored language even when it matches the cookie", () => {
    expect(resolveInitialLanguage(JSON.stringify("de"), "de")).toBeNull();
  });

  it("ignores a cookie naming a locale that is not supported", () => {
    expect(resolveInitialLanguage(null, "klingon")).toBeNull();
  });

  it("adopts nothing when there is no cookie either", () => {
    expect(resolveInitialLanguage(null, null)).toBeNull();
    expect(resolveInitialLanguage(null, "")).toBeNull();
  });
});
