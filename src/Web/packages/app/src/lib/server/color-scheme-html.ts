import {
  PREFS_COOKIE_NAME,
  parseColorSchemeCookie,
} from "$lib/stores/appearance-store.svelte";

const DARK_HTML_CLASS = '<html lang="en" class="dark">';
const LIGHT_HTML_CLASS = '<html lang="en">';

/**
 * App.html ships dark. Only a stored light scheme changes that: the server
 * cannot know the system preference, so `system` keeps the dark default and
 * mode-watcher's inline script corrects it.
 */
export function renderColorScheme(
  html: string,
  cookies: { get(name: string): string | undefined }
): string {
  if (parseColorSchemeCookie(cookies.get(PREFS_COOKIE_NAME)) !== "light")
    return html;
  return html.replace(DARK_HTML_CLASS, LIGHT_HTML_CLASS);
}
