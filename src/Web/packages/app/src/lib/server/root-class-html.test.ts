import { readFileSync } from "node:fs";
import { describe, expect, it, vi } from "vitest";

vi.mock("$app/env", () => ({ browser: false, dev: false }));
vi.mock("$app/navigation", () => ({}));
vi.mock("$app/state", () => ({}));
vi.mock("mode-watcher", () => ({
  setMode: vi.fn(),
  mode: { current: "light" },
  userPrefersMode: { current: "system" },
}));

const { renderRootClasses } = await import("./root-class-html");

const PAGE =
  '<!doctype html><html lang="en" class="dark"><head></head><body></body></html>';

const prefsCookie = (prefs: unknown) =>
  encodeURIComponent(JSON.stringify(prefs));
const cookiesOf = (value: string | undefined) => ({
  get: (name: string) => (name === "nocturne-prefs" ? value : undefined),
});
const member = (preferences?: object) =>
  ({
    isShareHost: false,
    isAuthenticated: true,
    isGuestSession: false,
    user: { preferences },
  }) as unknown as App.Locals;
const anonymous = {
  isShareHost: false,
  isAuthenticated: false,
  isGuestSession: false,
} as unknown as App.Locals;
const render = (value: string | undefined) =>
  renderRootClasses(PAGE, cookiesOf(value), anonymous);

describe("renderRootClasses", () => {
  it("drops the dark class from the shipped app.html", () => {
    const appHtml = readFileSync(
      new URL("../../app.html", import.meta.url),
      "utf8"
    );
    const html = renderRootClasses(
      appHtml,
      { get: () => prefsCookie({ colorScheme: "light" }) },
      anonymous
    );
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

describe("range palette class", () => {
  const html = (cookie: object | undefined, locals = anonymous) =>
    renderRootClasses(
      PAGE,
      cookiesOf(cookie && prefsCookie(cookie)),
      locals
    );

  it("adds the distinct palette's class from the device cookie", () => {
    expect(html({ glucoseRangePalette: "clarity" })).toContain(
      '<html lang="en" class="dark range-palette-clarity">'
    );
  });

  it("keeps it beside a light scheme", () => {
    expect(
      html({ colorScheme: "light", glucoseRangePalette: "clarity" })
    ).toContain('<html lang="en" class="range-palette-clarity">');
  });

  it("adds it from the member's saved preference", () => {
    expect(
      html(undefined, member({ glucoseRangePalette: "clarity" }))
    ).toContain("range-palette-clarity");
  });

  it("lets the member's saved preference win over the cookie", () => {
    expect(
      html(
        { glucoseRangePalette: "clarity" },
        member({ glucoseRangePalette: "theme" })
      )
    ).toBe(PAGE);
  });

  it("ignores the viewer's cookie on a share host", () => {
    const share = {
      isShareHost: true,
      isAuthenticated: false,
      isGuestSession: false,
    } as unknown as App.Locals;
    expect(html({ glucoseRangePalette: "clarity" }, share)).toBe(PAGE);
  });

  it.each([
    ["the theme palette", { glucoseRangePalette: "theme" }],
    ["no palette", { colorScheme: "dark" }],
    ["an unknown palette", { glucoseRangePalette: "neon" }],
    ["no cookie", undefined],
  ])("adds no class for %s", (_, cookie) => {
    expect(html(cookie)).toBe(PAGE);
  });
});
