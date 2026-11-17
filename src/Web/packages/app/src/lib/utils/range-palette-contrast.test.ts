import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const CSS = readFileSync(
  new URL("../../../../ui/src/styles/range-palette.css", import.meta.url),
  "utf8",
);

function token(name: string): [number, number, number] {
  const match = new RegExp(String.raw`--${name}:\s*oklch\(([\d.]+) ([\d.]+) ([\d.]+)\)`).exec(CSS);
  if (!match) throw new Error(`--${name} is not an oklch value in range-palette.css`);
  return [Number(match[1]), Number(match[2]), Number(match[3])];
}

function luminance([l, c, h]: [number, number, number]): number {
  const a = c * Math.cos((h * Math.PI) / 180);
  const b = c * Math.sin((h * Math.PI) / 180);
  const l_ = (l + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m_ = (l - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s_ = (l - 0.0894841775 * a - 1.291485548 * b) ** 3;
  const [r, g, bl] = [
    4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_,
    -1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_,
    -0.0041960863 * l_ - 0.7034186147 * m_ + 1.707614701 * s_,
  ].map((channel) => Math.min(1, Math.max(0, channel)));
  return 0.2126 * r + 0.7152 * g + 0.0722 * bl;
}

function contrast(fill: string, ink: string): number {
  const [x, y] = [luminance(token(fill)), luminance(token(ink))];
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}

describe("range-palette.css", () => {
  it.each(["very-low", "low", "high", "very-high"])(
    "keeps text on --glucose-%s at WCAG AA",
    (band) => {
      expect(contrast(`glucose-${band}`, `glucose-${band}-foreground`)).toBeGreaterThanOrEqual(4.5);
    },
  );
});
