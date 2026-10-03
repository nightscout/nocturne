import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("$app/environment", () => ({ browser: false, dev: false }));
vi.mock("mode-watcher", () => ({
  setMode: vi.fn(),
  mode: { current: "light" },
  userPrefersMode: { current: "system" },
}));

const { applyPreferences } =
  await import("$lib/stores/appearance-store.svelte");
const { a1cLabel, formatA1c, formatA1cNumber } =
  await import("./a1c-formatting");

describe("A1c presentation", () => {
  beforeEach(() => applyPreferences({ a1cName: "HbA1c", a1cUnits: "percent" }));

  it("distinguishes measured and estimated values with either preferred name", () => {
    expect(a1cLabel()).toBe("HbA1c");
    expect(a1cLabel(true)).toBe("eHbA1c");
    applyPreferences({ a1cName: "A1c" });
    expect(a1cLabel()).toBe("A1c");
    expect(a1cLabel(true)).toBe("eA1c");
  });

  it("formats the backend supplied value for the selected unit without recalculating it", () => {
    const value = { percent: 7, mmolMol: 53 };
    expect(formatA1c(value)).toBe("7.0%");
    applyPreferences({ a1cUnits: "mmol/mol" });
    expect(formatA1c(value)).toBe("53 mmol/mol");
    expect(formatA1c(undefined)).toBe("–");
    expect(formatA1cNumber(NaN)).toBe("–");
  });
});
