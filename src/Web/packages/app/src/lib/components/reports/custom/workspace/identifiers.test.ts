import { readdirSync, readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { vocabularyOf } from "./workspace.svelte.js";

/**
 * wuchale extracts a capitalised string literal in a `.svelte` or `.svelte.ts`
 * file as a message and swaps it for a catalogue lookup, so in another locale
 * an anchor or a weekday written there reaches the spec translated. The API's
 * identifiers come from the catalogue and stay in plain modules, which wuchale does not read.
 */
describe("identifiers the API reads", () => {
  const dir = new URL(".", import.meta.url);
  const files = readdirSync(dir).filter(
    (f) => /\.svelte(\.ts)?$/.test(f) && !f.endsWith(".test.ts")
  );
  const vocab = vocabularyOf(liveCatalogue);
  const identifiers = [...vocab.anchors.map((a) => a.id), ...vocab.weekdays];

  it.each(files)("are not written as literals in %s", (file) => {
    const source = readFileSync(new URL(file, dir), "utf8");
    const found = identifiers.filter((id) =>
      new RegExp(`["'\`]${id}["'\`]`).test(source)
    );
    expect(found).toEqual([]);
  });
});
