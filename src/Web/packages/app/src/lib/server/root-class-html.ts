import {
  PREFS_COOKIE_NAME,
  RANGE_PALETTE_CLASS,
  glucoseRangePalette,
  parseColorSchemeCookie,
} from "#lib/stores/appearance-store.svelte.js";
import {
  displayPreferenceLayers,
  displayPreferenceSources,
} from "#lib/server/display-preference-layers.js";

const DARK_HTML_CLASS = '<html lang="en" class="dark">';

/**
 * App.html ships dark. Only a stored light scheme changes that: the server
 * cannot know the system preference, so `system` keeps the dark default and
 * mode-watcher's inline script corrects it. The distinct range palette's class
 * comes from the same resolution as the plot snapshot's, without the owner's
 * presentation, which only a share or guest viewer needs and this point does not hold.
 */
export function renderRootClasses(
  html: string,
  cookies: { get(name: string): string | undefined },
  locals: App.Locals
): string {
  const classes: string[] = [];
  if (parseColorSchemeCookie(cookies.get(PREFS_COOKIE_NAME)) !== "light")
    classes.push("dark");
  const layers = displayPreferenceLayers(
    displayPreferenceSources(locals, cookies, null)
  );
  if (glucoseRangePalette.resolve(layers) === "clarity")
    classes.push(RANGE_PALETTE_CLASS);
  const attribute = classes.length ? ` class="${classes.join(" ")}"` : "";
  return html.replace(DARK_HTML_CLASS, `<html lang="en"${attribute}>`);
}
