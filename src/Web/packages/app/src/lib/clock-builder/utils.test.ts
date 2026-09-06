import { describe, it, expect } from "vitest";
import type { ClockElement } from "$lib/api";
import { clockBackgroundStyle, getElementColor } from "./utils";

const dynamic = (): ClockElement => ({
  type: "sg",
  style: { color: "dynamic" },
});

describe("getElementColor", () => {
  it("takes no glucose colour when there is no reading", () => {
    expect(getElementColor(dynamic(), null)).toBe("#ffffff");
  });

  it("keeps a fixed colour whatever the reading", () => {
    const fixed: ClockElement = { type: "sg", style: { color: "#ff0000" } };
    expect(getElementColor(fixed, null)).toBe("#ff0000");
    expect(getElementColor(fixed, 55)).toBe("#ff0000");
  });

  it("resolves the muted token to the theme's muted foreground", () => {
    const muted: ClockElement = { type: "delta", style: { color: "muted" } };
    expect(getElementColor(muted, null)).toBe("var(--muted-foreground)");
    expect(getElementColor(muted, 55)).toBe("var(--muted-foreground)");
  });

  it("takes the default rather than emitting an unknown token as CSS", () => {
    const unknown: ClockElement = { type: "sg", style: { color: "subtle" } };
    expect(getElementColor(unknown, null)).toBe("#ffffff");
    expect(getElementColor({ type: "sg", style: {} }, null)).toBe("#ffffff");
  });
});

describe("clockBackgroundStyle", () => {
  const settings = { bgColor: true };

  it("takes the fallback, not a glucose colour, when there is no reading", () => {
    expect(clockBackgroundStyle(settings, null, "#0a0a0a")).toBe(
      "background-color: #0a0a0a;"
    );
  });

  it("colours the face by a real reading", () => {
    expect(clockBackgroundStyle(settings, 55, "#0a0a0a")).not.toBe(
      "background-color: #0a0a0a;"
    );
  });

  it("takes the fallback when the face is not coloured by glucose", () => {
    expect(clockBackgroundStyle({}, 55, "#0a0a0a")).toBe(
      "background-color: #0a0a0a;"
    );
    expect(clockBackgroundStyle(undefined, 55, "#0a0a0a")).toBe(
      "background-color: #0a0a0a;"
    );
  });

  it("prefers a background image over either", () => {
    expect(
      clockBackgroundStyle(
        { bgColor: true, backgroundImage: "/face.png" },
        55,
        "#0a0a0a"
      )
    ).toContain("background-image: url(/face.png)");
  });
});
