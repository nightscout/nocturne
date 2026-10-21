import { readFileSync } from "node:fs";
import { describe, expect, it, vi } from "vitest";

vi.mock("$app/environment", () => ({ browser: false, dev: false }));
vi.mock("$app/navigation", () => ({}));
vi.mock("$app/state", () => ({}));
vi.mock("mode-watcher", () => ({
  setMode: vi.fn(),
  mode: { current: "light" },
  userPrefersMode: { current: "system" },
}));

const { renderColorScheme } = await import("./color-scheme-html");

const PAGE =
  '<!doctype html><html lang="en" class="dark"><head></head><body></body></html>';

const prefsCookie = (prefs: unknown) =>
  encodeURIComponent(JSON.stringify(prefs));
const render = (value: string | undefined) =>
  renderColorScheme(PAGE, {
    get: (name) => (name === "nocturne-prefs" ? value : undefined),
  });

describe("renderColorScheme", () => {
  it("drops the dark class from the shipped app.html", () => {
    const appHtml = readFileSync(
      new URL("../../app.html", import.meta.url),
      "utf8"
    );
    const html = renderColorScheme(appHtml, {
      get: () => prefsCookie({ colorScheme: "light" }),
    });
    expect(appHtml).toContain('class="dark"');
    expect(html).not.toContain('class="dark"');
  });

  it("renders a light scheme without the dark class", () => {
    const html = render(
      prefsCookie({ glucoseUnits: "mmol", colorScheme: "light" })
    );
    expect(html).not.toContain("dark");
    expect(html).toContain('<html lang="en">');
  });

  it.each([
    ["dark", prefsCookie({ colorScheme: "dark" })],
    ["system", prefsCookie({ colorScheme: "system" })],
    ["a missing cookie", undefined],
    ["a cookie without a scheme", prefsCookie({ glucoseUnits: "mmol" })],
    ["an unknown scheme", prefsCookie({ colorScheme: "sepia" })],
    ["a non-string scheme", prefsCookie({ colorScheme: 1 })],
    ["malformed JSON", "%7Bnot-json"],
    ["a malformed escape", "%E0%A4%A"],
    ["a non-object payload", prefsCookie("light")],
  ])("keeps the dark class for %s", (_, value) => {
    expect(render(value)).toBe(PAGE);
  });
});
