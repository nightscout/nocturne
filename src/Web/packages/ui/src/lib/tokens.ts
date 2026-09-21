/**
 * Status colours as values rather than CSS custom properties, for consumers
 * that have no browser to resolve a `var()` in: chat cards, e-mail, anything
 * running in Node.
 *
 * The themes disagree on these hues, and a colour baked into a sent message
 * cannot follow the reader's theme afterwards, so out-of-browser consumers are
 * defined to use the default theme. These literals are therefore the ones in
 * `src/styles/nocturne-theme.css`, and a guard test fails if that file drifts
 * from them. In-browser consumers keep using `statusVar`, which stays live.
 */
export const STATUS_TOKENS = {
  critical: "oklch(0.577 0.245 27.325)",
  warning: "oklch(0.646 0.222 41.116)",
  info: "oklch(0.62 0.16 250)",
  normal: "oklch(0.6 0.118 184.704)",
} as const;

export type StatusToken = keyof typeof STATUS_TOKENS;

/** The custom property a theme defines this status under. */
export function statusProperty(token: StatusToken): string {
  return `--status-${token}`;
}

/** Theme-following reference, for anything rendered in a browser. */
export function statusVar(token: StatusToken): string {
  return `var(${statusProperty(token)})`;
}

/** `#rrggbb`, e.g. for a Slack attachment. */
export function statusHex(token: StatusToken): string {
  return oklchToHex(STATUS_TOKENS[token]);
}

/** The same colour as the 24-bit integer a Discord embed takes. */
export function statusRgb(token: StatusToken): number {
  return Number.parseInt(statusHex(token).slice(1), 16);
}

/**
 * `oklch(L C H)` to `#rrggbb`, clipping to the sRGB gamut.
 *
 * Every theme states these colours in oklch, so a consumer that needs bytes has
 * to convert rather than carry a second spelling that can disagree.
 */
export function oklchToHex(css: string): string {
  const number = String.raw`(\d+(?:\.\d+)?)`;
  const match = new RegExp(
    String.raw`^oklch\(\s*${number}\s+${number}\s+${number}\s*\)$`,
  ).exec(css.trim());
  if (!match) {
    throw new Error(`Not an oklch() colour: ${css}`);
  }
  const lightness = Number(match[1]);
  const chroma = Number(match[2]);
  const hue = (Number(match[3]) * Math.PI) / 180;

  const a = chroma * Math.cos(hue);
  const b = chroma * Math.sin(hue);
  const l = (lightness + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m = (lightness - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s = (lightness - 0.0894841775 * a - 1.291485548 * b) ** 3;

  const channels = [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ];

  return `#${channels.map(toByte).map((v) => v.toString(16).padStart(2, "0")).join("")}`;
}

function toByte(linear: number): number {
  const clipped = Math.min(1, Math.max(0, linear));
  const encoded =
    clipped > 0.0031308 ? 1.055 * clipped ** (1 / 2.4) - 0.055 : 12.92 * clipped;
  return Math.round(encoded * 255);
}
