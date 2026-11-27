import { beforeAll, describe, expect, it } from "vitest";
import { loadLocales, runWithLocale } from "wuchale/load-utils/server";
import * as main from "../../../../../../../../locales/main.loader.server.svelte.js";
import { c as english } from "../../../../../../../../locales/.wuchale/main.0.en.compiled.js";
import { c as german } from "../../../../../../../../locales/.wuchale/main.0.de.compiled.js";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Workspace } from "./workspace.svelte.js";

type Compiled = (typeof english)[number];

/**
 * Gives the German catalogue a translation of a message it has none for yet. A message is found by
 * its English source, since the compiled catalogues share their numbering.
 */
function translate(source: Compiled, translation: Compiled) {
  const at = english.findIndex((message) => JSON.stringify(message) === JSON.stringify(source));
  expect(at, `no message ${JSON.stringify(source)}`).toBeGreaterThanOrEqual(0);
  german[at] = translation;
}

describe("a caption under the production translation transform", () => {
  beforeAll(async () => {
    await loadLocales(main.key, main.loadCount, main.loadCatalog, ["en", "de"]);
    translate("Insulin on board", "Insulin an Bord");
    translate("glucose", "Glukosewerte");
    translate([0, " against ", 1], [0, " gegen ", 1]);
  });

  const caption = () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("against");
    ws.toggleSignal("glucose");
    ws.toggleSignal("iob");
    return ws.caption()[0].text;
  };

  it("puts the first signal in its heading message and the second in its in-sentence message", async () => {
    expect(await runWithLocale("de", async () => caption())).toBe("Insulin an Bord gegen Glukosewerte");
  });

  it("keeps the English messages in English", async () => {
    expect(await runWithLocale("en", async () => caption())).toBe("Insulin on board against glucose");
  });
});
