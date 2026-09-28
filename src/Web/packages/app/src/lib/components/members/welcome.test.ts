import { describe, expect, it } from "vitest";
import { isWelcomeViewer, takeWelcome, withWelcome } from "./welcome";

describe("withWelcome", () => {
  it("marks a bare path", () => {
    expect(withWelcome("/")).toBe("/?welcome=1");
  });

  it("keeps the destination's own query and hash", () => {
    expect(withWelcome("/reports?range=7d#tir")).toBe(
      "/reports?range=7d&welcome=1#tir"
    );
  });
});

describe("takeWelcome", () => {
  it("returns nothing for a URL without the marker", () => {
    expect(takeWelcome(new URL("https://sam.example/?range=7d"))).toBeNull();
  });

  it("strips only the marker", () => {
    const rest = takeWelcome(
      new URL("https://sam.example/reports?range=7d&welcome=1#tir")
    );
    expect(rest?.href).toBe("https://sam.example/reports?range=7d#tir");
  });

  it("round-trips a marked path", () => {
    const marked = new URL(withWelcome("/?a=1"), "https://sam.example");
    expect(takeWelcome(marked)?.href).toBe("https://sam.example/?a=1");
  });
});

describe("isWelcomeViewer", () => {
  it("never welcomes an owner", () => {
    expect(isWelcomeViewer(["*"])).toBe(false);
  });

  it("welcomes a member or guest with narrower scopes", () => {
    expect(isWelcomeViewer(["glucose.read", "reports.read"])).toBe(true);
    expect(isWelcomeViewer([])).toBe(true);
  });
});
