/**
 * The CSS colour expressions the chart theme and the charts write (hex, rgb(), oklch() and its
 * relative form, color-mix(), var()), evaluated to sRGB, so a canvas with no stylesheet paints the
 * colour a browser computes for the same expression.
 */

/** sRGB channels and alpha, each 0 to 1. */
export interface Rgba {
  r: number;
  g: number;
  b: number;
  a: number;
}

/** Custom property values by name without the leading dashes, as declared (unresolved). */
export type CustomProperties = ReadonlyMap<string, string>;

const TRANSPARENT: Rgba = { r: 0, g: 0, b: 0, a: 0 };

const NAMED: Record<string, Rgba> = {
  transparent: TRANSPARENT,
  black: { r: 0, g: 0, b: 0, a: 1 },
  white: { r: 1, g: 1, b: 1, a: 1 },
};

const clamp01 = (v: number) => Math.min(1, Math.max(0, v));

/** The top-level parts of a text, split on commas or on whitespace outside parentheses. */
function splitTopLevel(text: string, separator: "," | " "): string[] {
  const parts: string[] = [];
  let depth = 0;
  let current = "";
  for (const ch of text) {
    if (ch === "(") depth++;
    if (ch === ")") depth--;
    const splits = depth === 0 && (separator === "," ? ch === "," : /\s/.test(ch));
    if (splits) {
      if (current.trim() !== "") parts.push(current.trim());
      current = "";
    } else {
      current += ch;
    }
  }
  if (current.trim() !== "") parts.push(current.trim());
  return parts;
}

/** `name(args)` as its name and inner text, or null for anything else. */
function call(value: string): { name: string; args: string } | null {
  const match = /^([a-z-]+)\((.*)\)$/is.exec(value.trim());
  return match ? { name: match[1].toLowerCase(), args: match[2] } : null;
}

/** A number, or a percentage of `percentOf`; NaN for anything else. */
function amount(token: string, percentOf = 1): number {
  const t = token.trim();
  if (t.endsWith("%")) return (parseFloat(t) / 100) * percentOf;
  return parseFloat(t);
}

function srgbToLinear(c: number): number {
  return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function linearToSrgb(c: number): number {
  return c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055;
}

/** OKLab to sRGB (Ottosson's matrices), clipped to the sRGB gamut. */
export function oklabToRgb(L: number, a: number, b: number, alpha = 1): Rgba {
  const l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s = (L - 0.0894841775 * a - 1.291485548 * b) ** 3;
  return {
    r: clamp01(linearToSrgb(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s)),
    g: clamp01(linearToSrgb(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s)),
    b: clamp01(linearToSrgb(-0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s)),
    a: clamp01(alpha),
  };
}

/** sRGB to OKLab. */
export function rgbToOklab({ r, g, b }: Rgba): [number, number, number] {
  const lr = srgbToLinear(r);
  const lg = srgbToLinear(g);
  const lb = srgbToLinear(b);
  const l = Math.cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
  const m = Math.cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
  const s = Math.cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);
  return [
    0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
    1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
    0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s,
  ];
}

function oklch(L: number, C: number, H: number, alpha: number): Rgba {
  const h = (H * Math.PI) / 180;
  return oklabToRgb(L, C * Math.cos(h), C * Math.sin(h), alpha);
}

function hex(text: string): Rgba | null {
  const digits = text.slice(1);
  if (!/^[0-9a-f]+$/i.test(digits)) return null;
  const full = digits.length <= 4 ? [...digits].map((d) => d + d).join("") : digits;
  if (full.length !== 6 && full.length !== 8) return null;
  const channel = (i: number) => parseInt(full.slice(i, i + 2), 16) / 255;
  return { r: channel(0), g: channel(2), b: channel(4), a: full.length === 8 ? channel(6) : 1 };
}

/** The channels and the alpha after a slash (`0.6 0.1 184 / 0.3`), or the fourth of four comma-separated values. */
function channelsAndAlpha(args: string): { channels: string[]; alpha: string | null } {
  const [main, alpha] = args.split("/");
  const channels = main.includes(",") ? splitTopLevel(main, ",") : splitTopLevel(main, " ");
  if (alpha === undefined && channels.length === 4) return { channels: channels.slice(0, 3), alpha: channels[3] };
  return { channels, alpha: alpha?.trim() ?? null };
}

function premultiplied(c: Rgba, space: "oklab" | "srgb"): [number, number, number] {
  const [x, y, z] = space === "oklab" ? rgbToOklab(c) : [c.r, c.g, c.b];
  return [x * c.a, y * c.a, z * c.a];
}

/** color-mix() as CSS Color 5 defines it: premultiplied alpha, percentages normalised, a sum under 100% scales the alpha. */
function mix(args: string, vars: CustomProperties, depth: number): Rgba | null {
  const [space, first, second] = splitTopLevel(args, ",");
  const interpolation = /^in\s+(oklab|srgb)$/i.exec(space?.trim() ?? "")?.[1]?.toLowerCase();
  if ((interpolation !== "oklab" && interpolation !== "srgb") || !first || !second) return null;

  const part = (text: string) => {
    const tokens = splitTopLevel(text, " ");
    const percent = tokens.find((t) => t.endsWith("%"));
    const colour = tokens.filter((t) => t !== percent).join(" ");
    return { colour: evaluate(colour, vars, depth + 1), percent: percent === undefined ? null : parseFloat(percent) };
  };
  const a = part(first);
  const b = part(second);
  if (!a.colour || !b.colour) return null;

  let p1 = a.percent ?? (b.percent === null ? 50 : 100 - b.percent);
  let p2 = b.percent ?? 100 - p1;
  const sum = p1 + p2;
  if (sum <= 0) return null;
  const scale = Math.min(sum, 100) / 100;
  p1 /= sum;
  p2 /= sum;

  const alpha = a.colour.a * p1 + b.colour.a * p2;
  if (alpha === 0) return TRANSPARENT;
  const ca = premultiplied(a.colour, interpolation);
  const cb = premultiplied(b.colour, interpolation);
  const [x, y, z] = [0, 1, 2].map((i) => (ca[i] * p1 + cb[i] * p2) / alpha);
  const out = interpolation === "oklab"
    ? oklabToRgb(x, y, z, alpha)
    : { r: clamp01(x), g: clamp01(y), b: clamp01(z), a: alpha };
  return { ...out, a: clamp01(out.a * scale) };
}

function evaluate(value: string, vars: CustomProperties, depth: number): Rgba | null {
  if (depth > 16) return null;
  const text = value.trim();
  const lower = text.toLowerCase();
  if (lower in NAMED) return NAMED[lower];
  if (text.startsWith("#")) return hex(text);

  const fn = call(text);
  if (!fn) return null;

  if (fn.name === "var") {
    const [name, ...fallback] = splitTopLevel(fn.args, ",");
    const declared = vars.get(name.trim().replace(/^--/, ""));
    if (declared !== undefined) return evaluate(declared, vars, depth + 1);
    return fallback.length > 0 ? evaluate(fallback.join(","), vars, depth + 1) : null;
  }

  if (fn.name === "color-mix") return mix(fn.args, vars, depth);

  if (fn.name === "rgb" || fn.name === "rgba") {
    const { channels, alpha } = channelsAndAlpha(fn.args);
    if (channels.length !== 3) return null;
    const [r, g, b] = channels.map((c) => amount(c, 255) / 255);
    const a = alpha === null ? 1 : amount(alpha);
    return [r, g, b, a].some(Number.isNaN) ? null : { r: clamp01(r), g: clamp01(g), b: clamp01(b), a: clamp01(a) };
  }

  if (fn.name === "oklch") {
    const relative = /^from\s+/i.exec(fn.args.trim());
    if (relative) {
      // Only the form the theme writes: `oklch(from <colour> l c h [/ alpha])`, the colour with a new alpha.
      const rest = fn.args.trim().slice(relative[0].length);
      const slash = rest.lastIndexOf("/");
      const tokens = splitTopLevel(slash >= 0 ? rest.slice(0, slash) : rest, " ");
      if (tokens.length !== 4 || tokens.slice(1).join(" ").toLowerCase() !== "l c h") return null;
      const origin = evaluate(tokens[0], vars, depth + 1);
      if (!origin) return null;
      return slash < 0 ? origin : { ...origin, a: clamp01(amount(rest.slice(slash + 1))) };
    }
    const { channels, alpha } = channelsAndAlpha(fn.args);
    if (channels.length !== 3) return null;
    const L = amount(channels[0]);
    const C = amount(channels[1], 0.4);
    const H = parseFloat(channels[2]);
    const a = alpha === null ? 1 : amount(alpha);
    return [L, C, H, a].some(Number.isNaN) ? null : oklch(L, C, H, a);
  }

  return null;
}

/** A CSS colour expression in sRGB, its `var()`s read from `vars`; null when it is not a colour this reads. */
export function parseCssColor(value: string, vars: CustomProperties = new Map()): Rgba | null {
  return evaluate(value, vars, 0);
}

/** `#rrggbb`, or `#rrggbbaa` when not opaque. */
export function toHex({ r, g, b, a }: Rgba): string {
  const byte = (v: number) => Math.round(clamp01(v) * 255).toString(16).padStart(2, "0");
  return `#${byte(r)}${byte(g)}${byte(b)}${a >= 1 ? "" : byte(a)}`;
}

/** A CSS colour expression as hex, or null when it is not a colour this reads. */
export function cssColorToHex(value: string, vars: CustomProperties = new Map()): string | null {
  const colour = parseCssColor(value, vars);
  return colour ? toHex(colour) : null;
}

/** A custom property declared where the theme reader does not look: what it was, and in which block. */
export interface UnreadDeclaration {
  selector: string;
  name: string;
}

/** Whether a selector list addresses the dark scheme's root: `.dark`, `:root.dark`, `html.dark`. */
function isDarkSelector(selector: string): boolean {
  return selector.split(",").some((s) => /^(:root|html)?\.dark$/.test(s.trim()));
}

/** Whether a selector list addresses the document root alone. */
function isRootSelector(selector: string): boolean {
  return selector.split(",").every((s) => /^(:root|html)$/.test(s.trim()));
}

/** `css` without its comments; an unclosed comment runs to the end, as in a browser. Linear, unlike a lazy regex. */
function stripComments(css: string): string {
  let out = "";
  let at = 0;
  for (let open = css.indexOf("/*"); open !== -1; open = css.indexOf("/*", at)) {
    out += css.slice(at, open);
    const close = css.indexOf("*/", open + 2);
    if (close === -1) return out;
    at = close + 2;
  }
  return out + css.slice(at);
}

/**
 * The custom properties a stylesheet declares for the light scheme (`:root`) and the dark one
 * (`.dark`, `:root.dark`, or `:root` inside `@media (prefers-color-scheme: dark)`), inside any other
 * at-rule, as written. A later declaration of a name replaces an earlier one, as in the cascade.
 * Declarations in any other block (except Tailwind's `@theme`, which only maps them to utilities)
 * are returned as `unread`, so a test can refuse a stylesheet the reader would misread.
 */
export function readCustomProperties(css: string): {
  root: Map<string, string>;
  dark: Map<string, string>;
  unread: UnreadDeclaration[];
} {
  const source = stripComments(css);
  const root = new Map<string, string>();
  const dark = new Map<string, string>();
  const unread: UnreadDeclaration[] = [];
  const open: { selector: string; body: string }[] = [];
  let text = "";
  for (const ch of source) {
    if (ch === "{") {
      // The selector is what follows the last statement of the enclosing block.
      const cut = text.lastIndexOf(";") + 1;
      const selector = text.slice(cut).trim();
      if (open.length > 0) open[open.length - 1].body += text.slice(0, cut);
      open.push({ selector, body: "" });
      text = "";
    } else if (ch === "}") {
      const block = open.pop();
      if (block) {
        block.body += text;
        const inDarkMedia = open.some((b) => /^@media\b.*prefers-color-scheme\s*:\s*dark/i.test(b.selector));
        const target = isDarkSelector(block.selector) || (inDarkMedia && isRootSelector(block.selector))
          ? dark
          : isRootSelector(block.selector) ? root : null;
        for (const [name, value] of declarations(block.body)) {
          if (target) target.set(name, value);
          else if (!block.selector.startsWith("@theme")) unread.push({ selector: block.selector, name });
        }
      }
      text = "";
    } else {
      text += ch;
    }
  }
  return { root, dark, unread };
}

/** `--name: value;` pairs in a block's own text; a value written over several lines is joined. */
function declarations(body: string): [string, string][] {
  const found: [string, string][] = [];
  for (const statement of body.split(";")) {
    const match = /^\s*--([a-z0-9-]+)\s*:\s*([\s\S]+?)\s*$/i.exec(statement);
    if (match) found.push([match[1], match[2].replace(/\s+/g, " ").replace(/\(\s+/g, "(").replace(/\s+\)/g, ")")]);
  }
  return found;
}
