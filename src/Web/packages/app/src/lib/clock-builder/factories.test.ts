import { describe, it, expect } from "vitest";
import { DEFAULT_SETTINGS } from "./types";
import { createDefaultConfig } from "./factories";

describe("createDefaultConfig", () => {
  it("hands out a copy of the default settings, not the module's own", () => {
    const original = DEFAULT_SETTINGS.staleMinutes;
    const settings = createDefaultConfig().settings;
    settings!.staleMinutes = (original ?? 0) + 5;

    expect(DEFAULT_SETTINGS.staleMinutes).toBe(original);
    expect(createDefaultConfig().settings?.staleMinutes).toBe(original);
  });
});
