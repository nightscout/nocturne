import type { Component } from "svelte";
import { render } from "svelte/server";
import { runWithLocale } from "wuchale/load-utils/server";
import type { SupportedLocale } from "#lib/stores/appearance-store.svelte.js";
import RequestLanguage from "./RequestLanguage.svelte";

type View<P extends object> = { component: Component<P>; viewProps: P } | { read: () => string };

/** The text of a view rendered on the server in `language`, with the catalogue and the list words of that language. */
export async function renderInLanguage<P extends object>(language: SupportedLocale, view: View<P>): Promise<string> {
  const { body } = await runWithLocale(language, async () => render(RequestLanguage<P>, { props: { language, ...view } }));
  return visibleText(body);
}

/** Markup without its comments (Svelte's hydration markers), each tag read as a space. */
function visibleText(html: string): string {
  let text = "";
  let at = 0;
  while (at < html.length) {
    if (html.startsWith("<!--", at)) {
      const end = html.indexOf("-->", at + 4);
      at = end < 0 ? html.length : end + 3;
    } else if (html[at] === "<") {
      const end = html.indexOf(">", at);
      text += " ";
      at = end < 0 ? html.length : end + 1;
    } else {
      text += html[at++];
    }
  }
  return text.replace(/\s+/g, " ").trim();
}
