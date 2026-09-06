import { describe, expect, it } from "vitest";
import { timeAgo } from "./index";

const NOW = Date.UTC(2026, 7, 29, 14, 5);

// `Intl.RelativeTimeFormat` is declared read-only, so the counting double is
// installed through a mutable view of the namespace.
const intl = Intl as { RelativeTimeFormat: typeof Intl.RelativeTimeFormat };

/** How many `Intl.RelativeTimeFormat`s `run` constructs. */
function countFormatters(run: () => void): number {
  const real = intl.RelativeTimeFormat;
  let built = 0;
  intl.RelativeTimeFormat = class extends real {
    constructor(...args: ConstructorParameters<typeof real>) {
      built++;
      super(...args);
    }
  };
  try {
    run();
  } finally {
    intl.RelativeTimeFormat = real;
  }
  return built;
}

describe("timeAgo", () => {
  it("builds one formatter per locale, however many times it is called", () => {
    // Once per call is a real cost: `timeAgo` is read from deriveds bound to a
    // ticking clock, and once per row in a device list.
    const built = countFormatters(() => {
      for (let i = 0; i < 5; i++) timeAgo(NOW - 60_000, "en-GB", NOW);
    });
    expect(built).toBe(1);
  });

  it("caches a tag ICU answers under a shorter name", () => {
    // `new Intl.RelativeTimeFormat("nb-NO").resolvedOptions().locale` is "nb", so a
    // cache keyed on the resolved tag never matches what was asked for.
    expect(new Intl.RelativeTimeFormat("nb-NO").resolvedOptions().locale).toBe(
      "nb"
    );

    const built = countFormatters(() => {
      for (let i = 0; i < 5; i++) timeAgo(NOW - 60_000, "nb-NO", NOW);
    });
    expect(built).toBe(1);
  });

  it("rebuilds when the locale actually changes", () => {
    const built = countFormatters(() => {
      timeAgo(NOW - 60_000, "en-GB", NOW);
      timeAgo(NOW - 60_000, "de-DE", NOW);
    });
    expect(built).toBe(2);
  });
});
