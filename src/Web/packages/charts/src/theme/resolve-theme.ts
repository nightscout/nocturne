import uiTokensCss from "@nocturne/ui/styles/nocturne-theme.css?raw";
import uiThemeCss from "@nocturne/ui/theme.css?raw";
import chartThemeCss from "../themes/nocturne.css?raw";
import { cssColorToHex, readCustomProperties } from "./css-color.js";
import type { StaticTheme } from "./static-theme.js";

/**
 * The stylesheets a page drawing the charts applies, in cascade order: the design system's tokens,
 * its chrome (background, foreground, borders, chart-1 to chart-5), then the clinical colours, which
 * `themes/nocturne.css` alone defines.
 */
const SHEETS = [uiTokensCss, uiThemeCss, chartThemeCss].map(readCustomProperties);

/** The family the static charts set their labels in; the host registers the app's Cabin under it. */
export const STATIC_FONT_FAMILY = "Cabin";

function variablesFor(mode: "light" | "dark"): Map<string, string> {
  const variables = new Map<string, string>();
  for (const sheet of SHEETS) for (const [name, value] of sheet.root) variables.set(name, value);
  if (mode === "dark")
    for (const sheet of SHEETS) for (const [name, value] of sheet.dark) variables.set(name, value);
  return variables;
}

const THEMES: Record<"light" | "dark", StaticTheme> = {
  light: { mode: "light", variables: variablesFor("light"), fontFamily: STATIC_FONT_FAMILY },
  dark: { mode: "dark", variables: variablesFor("dark"), fontFamily: STATIC_FONT_FAMILY },
};

/** The theme a static chart paints in: the page's light or dark scheme, read from the stylesheets once. */
export function resolveChartTheme(mode: "light" | "dark"): StaticTheme {
  return THEMES[mode];
}

/** Every colour custom property of a scheme, resolved to hex; values that are not colours are left out. */
export function themeColors(mode: "light" | "dark"): Record<string, string> {
  const { variables } = THEMES[mode];
  const colors: Record<string, string> = {};
  for (const name of variables.keys()) {
    const hex = cssColorToHex(`var(--${name})`, variables);
    if (hex) colors[name] = hex;
  }
  return colors;
}

/** The custom properties `themes/nocturne.css` declares, light and dark: what the clinical colours are. */
export function clinicalColorNames(): { light: string[]; dark: string[] } {
  const { root, dark } = readCustomProperties(chartThemeCss);
  return { light: [...root.keys()], dark: [...dark.keys()] };
}
