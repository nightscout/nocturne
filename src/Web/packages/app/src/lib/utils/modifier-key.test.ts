import { describe, expect, it } from "vitest";
import { isMacPlatform } from "@nocturne/ui/modifier-key";

describe("isMacPlatform", () => {
  it("is true for a Mac platform", () => {
    expect(isMacPlatform({ platform: "MacIntel" })).toBe(true);
    expect(isMacPlatform({ userAgentData: { platform: "macOS" } })).toBe(true);
  });

  it("is false for Windows and Linux", () => {
    expect(isMacPlatform({ platform: "Win32" })).toBe(false);
    expect(isMacPlatform({ platform: "Linux x86_64" })).toBe(false);
    expect(isMacPlatform({ userAgentData: { platform: "Windows" } })).toBe(false);
  });

  it("prefers userAgentData over the legacy platform", () => {
    expect(isMacPlatform({ platform: "MacIntel", userAgentData: { platform: "Windows" } })).toBe(false);
  });

  it("is false when no platform is reported", () => {
    expect(isMacPlatform({})).toBe(false);
  });
});
