import { describe, it, expect } from "vitest";
import type { ClockElement } from "$lib/api";
import {
  buildStyleString,
  clockBackgroundStyle,
  getBgColor,
  getElementColor,
} from "./utils";

const dynamic = (): ClockElement => ({
  type: "sg",
  style: { color: "dynamic" },
});

describe("getBgColor", () => {
  it.each([
    [53, "var(--glucose-very-low)"],
    [54, "var(--glucose-low)"],
    [69, "var(--glucose-low)"],
    [70, "var(--glucose-in-range)"],
    [180, "var(--glucose-in-range)"],
    [181, "var(--glucose-high)"],
    [250, "var(--glucose-high)"],
    [251, "var(--glucose-very-high)"],
  ])("gives %i the band colour as a CSS variable", (bg, expected) => {
    expect(getBgColor(bg)).toBe(expected);
  });
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

  it("paints a dynamic element in the reading's band colour", () => {
    expect(getElementColor(dynamic(), 40)).toBe("var(--glucose-very-low)");
    expect(getElementColor(dynamic(), 120)).toBe("var(--glucose-in-range)");
  });

  it("takes the default rather than emitting an unknown token as CSS", () => {
    const unknown: ClockElement = { type: "sg", style: { color: "subtle" } };
    expect(getElementColor(unknown, null)).toBe("#ffffff");
    expect(getElementColor({ type: "sg", style: {} }, null)).toBe("#ffffff");
  });

  it("does not resolve a token off the token map's prototype", () => {
    for (const inherited of ["constructor", "toString", "__proto__"]) {
      const element: ClockElement = { type: "sg", style: { color: inherited } };
      expect(getElementColor(element, null)).toBe("#ffffff");
    }
  });

  it("takes the default for a CSS colour that is not a hex literal", () => {
    for (const css of ["red", "rgb(255 0 0)", "hsl(0 100% 50%)"]) {
      const element: ClockElement = { type: "sg", style: { color: css } };
      expect(getElementColor(element, null)).toBe("#ffffff");
    }
  });
});

describe("buildStyleString", () => {
  it("scales the font size the caller renders at", () => {
    const element: ClockElement = { type: "sg", size: 20 };
    expect(buildStyleString(element, null, 2)).toContain("font-size: 40px");
    expect(buildStyleString(element, null, 0.8)).toContain("font-size: 16px");
  });

  it("sizes an element that carries no size off its type's default", () => {
    expect(buildStyleString({ type: "sg" }, null, 1)).toContain(
      "font-size: 40px"
    );
    expect(buildStyleString({ type: "age" }, null, 1)).toContain(
      "font-size: 10px"
    );
  });

  it("sizes an element of an unknown type off the global default", () => {
    expect(buildStyleString({ type: "wormhole" }, null, 1)).toContain(
      "font-size: 20px"
    );
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
    expect(clockBackgroundStyle(settings, 55, "#0a0a0a")).toBe(
      "background-color: var(--glucose-low);"
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
