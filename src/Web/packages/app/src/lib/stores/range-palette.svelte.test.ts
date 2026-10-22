import type {} from "@vitest/browser-playwright";
import { describe, expect, it } from "vitest";
import { setGlucoseRangePalette } from "./appearance-store.svelte";

const BANDS = [
  "--glucose-very-low",
  "--glucose-low",
  "--glucose-in-range",
  "--glucose-high",
  "--glucose-very-high",
] as const;

const CLARITY = {
  "--glucose-very-low": "oklch(0.42 0.15 18)",
  "--glucose-low": "oklch(0.6 0.22 27)",
  "--glucose-high": "oklch(0.86 0.17 95)",
  "--glucose-very-high": "oklch(0.72 0.18 55)",
};

const THEMES = {
  "": {
    "--glucose-very-low": "oklch(0.577 0.245 27.325)",
    "--glucose-low": "oklch(0.646 0.222 41.116)",
    "--glucose-in-range": "oklch(0.6 0.118 184.704)",
    "--glucose-high": "oklch(0.65 0.18 270)",
    "--glucose-very-high": "oklch(0.55 0.25 15)",
  },
  "trio-theme": {
    "--glucose-very-low": "rgb(235, 87, 87)",
    "--glucose-low": "rgb(255, 193, 69)",
    "--glucose-in-range": "rgb(111, 207, 151)",
    "--glucose-high": "rgb(255, 193, 69)",
    "--glucose-very-high": "rgb(235, 87, 87)",
  },
  "aaps-theme": {
    "--glucose-very-low": "oklch(0.55 0.25 29)",
    "--glucose-low": "oklch(0.8 0.2 100)",
    "--glucose-in-range": "oklch(0.75 0.3 142)",
    "--glucose-high": "oklch(0.8 0.2 100)",
    "--glucose-very-high": "oklch(0.55 0.25 29)",
  },
  "classic-theme": {
    "--glucose-very-low": "oklch(0.55 0.25 29)",
    "--glucose-low": "oklch(0.9 0.18 105)",
    "--glucose-in-range": "oklch(0.8 0.35 142)",
    "--glucose-high": "oklch(0.9 0.18 105)",
    "--glucose-very-high": "oklch(0.55 0.25 29)",
  },
} as const;

const read = () =>
  Object.fromEntries(
    BANDS.map((band) => [
      band,
      getComputedStyle(document.documentElement).getPropertyValue(band).trim(),
    ]),
  );

describe("glucose range palette", () => {
  it.each(Object.keys(THEMES) as (keyof typeof THEMES)[])(
    "clarity overrides the four out-of-range tokens and theme restores them on '%s'",
    (theme) => {
      const root = document.documentElement;
      const original = root.className;
      try {
        for (const dark of [false, true]) {
          root.className = dark ? `${theme} dark`.trim() : theme;

          setGlucoseRangePalette("clarity");
          expect(read()).toEqual({
            ...CLARITY,
            "--glucose-in-range": THEMES[theme]["--glucose-in-range"],
          });

          setGlucoseRangePalette("theme");
          expect(read()).toEqual(THEMES[theme]);
        }
      } finally {
        setGlucoseRangePalette("theme");
        root.className = original;
      }
    },
  );
});
