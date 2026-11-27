import { expect } from "vitest";
import { loadLocales } from "wuchale/load-utils/server";
import * as main from "../../../../../locales/main.loader.server.svelte.js";

type Compiled = string | (string | number)[];

type Key = string | string[] | { text: string; context?: string };

/** A message by its English source and context, and its German translation (the source when none is given). */
export interface Seed {
  text: string;
  context?: string;
  /** With `{0}`, `{1}` for the parameters in the order the translation puts them. */
  de?: string;
}

const catalogues = new URL("../../../../../locales/.wuchale/", import.meta.url).href;

/** Vite drops a `t` query as an HMR timestamp, so a manifest read again under it would be the first read. */
const manifestNow = (): Promise<{ keys: Key[] }> => import(/* @vite-ignore */ `${catalogues}main.0.manifest.js?read=${Date.now()}`);

/** The compiled catalogue the loader serves, which is the module the seeds change in place. */
const compiledOf = (language: "en" | "de"): Promise<{ c: Compiled[] }> => import(/* @vite-ignore */ `${catalogues}main.0.${language}.compiled.js`);

const compiled = (text: string): Compiled =>
  text
    .split(/\{(\d+)\}/)
    .flatMap<string | number>((part, i) => (i % 2 === 1 ? [Number(part)] : part === "" ? [] : [part]));

const isMessage = (key: Key, seed: Seed) =>
  seed.context === undefined ? key === seed.text : typeof key === "object" && !Array.isArray(key) && key.context === seed.context && key.text === seed.text;

/**
 * Gives the English and German catalogues the messages of `seeds`, so a render in German shows
 * what the code under test extracted: a message the transform never extracted keeps its English
 * source. The translation plugin writes the manifest as it transforms a module, so import the
 * module under test first; this waits for its messages. Returns a function that puts the
 * catalogues back.
 */
export async function seedCatalogue(seeds: Seed[], timeoutMs = 100_000): Promise<() => void> {
  const deadline = Date.now() + timeoutMs;
  let keys: Key[] = [];
  while (Date.now() < deadline) {
    keys = await manifestNow().then((module) => module.keys, () => keys);
    if (seeds.every((seed) => keys.some((key) => isMessage(key, seed)))) break;
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  const english = (await compiledOf("en")).c;
  const german = (await compiledOf("de")).c;
  const edits: [Compiled[], number, Compiled][] = [];
  for (const seed of seeds) {
    const at = keys.findIndex((key) => isMessage(key, seed));
    expect(at, `the transform did not extract ${JSON.stringify(seed.text)}${seed.context ? ` in ${seed.context}` : ""}`).toBeGreaterThanOrEqual(0);
    edits.push([english, at, english[at]], [german, at, german[at]]);
    english[at] = compiled(seed.text);
    german[at] = compiled(seed.de ?? seed.text);
  }
  await loadLocales(main.key, main.loadCount, main.loadCatalog, ["en", "de"]);
  return () => {
    for (const [catalogue, at, original] of edits.reverse()) catalogue[at] = original;
  };
}
