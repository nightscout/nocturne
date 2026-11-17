import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const modeWatcher = vi.hoisted(() => ({ preferred: "system" }));

vi.mock("mode-watcher", () => ({
  setMode: (value: string) => {
    modeWatcher.preferred = value;
  },
  mode: { current: "dark" },
  userPrefersMode: {
    get current() {
      return modeWatcher.preferred;
    },
  },
}));

const {
  PREFS_COOKIE_NAME,
  glucoseUnits,
  setColorScheme,
  parseColorSchemeCookie,
  parsePrefsCookie,
  readCookieFrom,
} = await import("./appearance-store.svelte");

const storedCookie = () => readCookieFrom(document.cookie, PREFS_COOKIE_NAME);

describe("color scheme cookie mirroring", () => {
  beforeEach(() => {
    document.cookie = `${PREFS_COOKIE_NAME}=;path=/;max-age=0`;
    modeWatcher.preferred = "system";
  });

  afterEach(() => {
    glucoseUnits.current = "mg/dl";
    document.cookie = `${PREFS_COOKIE_NAME}=;path=/;max-age=0`;
  });

  it.each(["light", "dark", "system"] as const)(
    "writes %s into the prefs cookie when set",
    (scheme) => {
      setColorScheme(scheme);
      expect(parseColorSchemeCookie(storedCookie())).toBe(scheme);
    }
  );

  it("keeps the synced preferences already in the cookie", () => {
    glucoseUnits.current = "mmol";
    setColorScheme("light");
    expect(parsePrefsCookie(storedCookie())).toMatchObject({
      colorScheme: "light",
    });
    document.cookie = `${PREFS_COOKIE_NAME}=${encodeURIComponent(JSON.stringify({ timeFormat: "24" }))};path=/`;
    setColorScheme("dark");
    expect(parsePrefsCookie(storedCookie())).toMatchObject({
      timeFormat: "24",
      colorScheme: "dark",
    });
  });

  it("replaces the previous scheme rather than adding a second cookie", () => {
    setColorScheme("light");
    setColorScheme("dark");
    const rows = document.cookie
      .split("; ")
      .filter((row) => row.startsWith(`${PREFS_COOKIE_NAME}=`));
    expect(rows).toHaveLength(1);
    expect(parseColorSchemeCookie(storedCookie())).toBe("dark");
  });
});
