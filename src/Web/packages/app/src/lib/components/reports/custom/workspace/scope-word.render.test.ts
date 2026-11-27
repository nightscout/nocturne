import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { renderInLanguage } from "#lib/test-fixtures/render-in-language.js";
import { seedCatalogue } from "#lib/test-stubs/catalogue-seed.js";
import { scopeWord } from "./vocabulary-words.svelte.js";

let restore: () => void;

beforeAll(async () => {
  restore = await seedCatalogue([{ text: "therapy settings", de: "Therapieeinstellungen" }]);
}, 120_000);

afterAll(() => restore());

describe("the word for an access scope, under the production translation transform", () => {
  it("translates the word and never the scope that names it", async () => {
    expect(await renderInLanguage("de", { read: () => scopeWord("therapy.read") })).toBe("Therapieeinstellungen");
    expect(await renderInLanguage("de", { read: () => scopeWord("unknown.read") })).toBe("unknown.read");
  });
});
