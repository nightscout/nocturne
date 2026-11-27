import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { loadLocales } from "wuchale/load-utils/server";
import * as main from "../../../../../../../../locales/main.loader.server.svelte.js";
import { EventAnchorKind } from "#lib/api/index.js";
import { renderInLanguage } from "#lib/test-fixtures/render-in-language.js";

type Compiled = string | (string | number)[];

type Manifest = (string | string[] | { text: string; context?: string })[];

let manifest: Manifest;
let english: Compiled[];
let german: Compiled[];
let Workspace: typeof import("./workspace.svelte.js").Workspace;
let liveCatalogue: typeof import("./catalogue.fixtures.js").liveCatalogue;

const edits: [Compiled[], number, Compiled | undefined][] = [];

const same = (a: Compiled, b: Compiled) => JSON.stringify(a) === JSON.stringify(b);

function set(catalogue: Compiled[], at: number, message: Compiled) {
  edits.push([catalogue, at, catalogue[at]]);
  catalogue[at] = message;
}

/** A message as the catalogues hold it: the text and the numbers of its parameters, in order. */
const compiled = (text: string): Compiled =>
  text
    .split(/\{(\d+)\}/)
    .flatMap<string | number>((part, i) => (i % 2 === 1 ? [Number(part)] : part === "" ? [] : [part]));

/**
 * Gives the German catalogue a translation of a message it has none for yet. A message is found by
 * its English source, since the compiled catalogues share their numbering.
 */
function translate(source: Compiled, translation: Compiled) {
  const at = english.findIndex((message) => same(message, source));
  expect(at, `no message ${JSON.stringify(source)}`).toBeGreaterThanOrEqual(0);
  set(german, at, translation);
}

/**
 * Gives both catalogues the message of a source and a context, as the manifest numbers it. The
 * plugin writes the compiled catalogues of a message it has just extracted only after the run, so
 * the English one is put in as well; with a translation the German one is too.
 */
function seed(context: string, source: string, translation?: Compiled) {
  const at = manifest.findIndex(
    (key) => typeof key === "object" && !Array.isArray(key) && key.context === context && key.text === source
  );
  expect(at, `no message ${JSON.stringify(source)} in ${context}`).toBeGreaterThanOrEqual(0);
  set(english, at, compiled(source));
  set(german, at, translation ?? compiled(source));
}

/** Waits for the translation plugin to write the manifest with the messages of the module under test, which it does as it transforms it. */
async function waitForContexts(contexts: string[]) {
  const fresh = () =>
    import(/* @vite-ignore */ `${new URL("../../../../../../../../locales/.wuchale/main.0.manifest.js", import.meta.url).href}?read=${Date.now()}`);
  const deadline = Date.now() + 100_000;
  while (Date.now() < deadline) {
    const keys: Manifest = await fresh().then((module) => module.keys, () => []);
    if (contexts.every((context) => keys.some((key) => typeof key === "object" && !Array.isArray(key) && key.context === context))) return;
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  expect.unreachable(`the messages of ${contexts.join(", ")} were not written`);
}

beforeAll(async () => {
  ({ Workspace } = await import("./workspace.svelte.js"));
  ({ liveCatalogue } = await import("./catalogue.fixtures.js"));
  await waitForContexts(["moment-in-sentence", "moment-heading", "when-caption", "caption-clause", "caption-clause-without-signals"]);
  manifest = (await import("../../../../../../../../locales/.wuchale/main.0.manifest.js")).keys as Manifest;
  english = (await import("../../../../../../../../locales/.wuchale/main.0.en.compiled.js")).c as Compiled[];
  german = (await import("../../../../../../../../locales/.wuchale/main.0.de.compiled.js")).c as Compiled[];
  seed("caption-clause", "{0} {1} {2}");
  seed("caption-clause-without-signals", "{0} {1}");
  await loadLocales(main.key, main.loadCount, main.loadCatalog, ["en", "de"]);
}, 120_000);

afterAll(() => {
  for (const [catalogue, at, original] of edits.reverse()) catalogue[at] = original as Compiled;
});

describe("a caption under the production translation transform", () => {
  beforeAll(() => {
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
    expect(await renderInLanguage("de", { read: caption })).toBe("Insulin an Bord gegen Glukosewerte");
  });

  it("keeps the English messages in English", async () => {
    expect(await renderInLanguage("en", { read: caption })).toBe("Insulin on board against glucose");
  });
});

describe("an event caption under the production translation transform", () => {
  const momentView = () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("event");
    ws.toggleSignal("glucose");
    ws.addMoment(EventAnchorKind.Wake);
    ws.updateMoment(0, { insulinOnBoard: { min: 0.5, max: 3 } });
    return ws.momentViews[0].text;
  };

  const momentHeading = () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("event");
    ws.toggleSignal("glucose");
    ws.updateMoment(0, { insulinOnBoard: { min: 0.5, max: 3 } });
    return ws.groupHeading(0);
  };

  const whenCaption = () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("event");
    ws.setShow("when");
    ws.addMoment(EventAnchorKind.Wake);
    return ws.caption()[0].text;
  };

  beforeAll(() => {
    seed("moment-in-sentence", "{0} {1}", [1, " bei ", 0]);
    seed("moment-heading", "{0} {1}");
    seed("when-caption", "{0}, {1}", [1, ": ", 0]);
  });

  it("lays a moment's anchor and its narrowings out as the message of that sentence does", async () => {
    expect(await renderInLanguage("de", { read: momentView })).toBe(
      "aktives Insulin (IOB) 0.5 bis 3 U beim Ereignis bei Mahlzeiten"
    );
    expect(await renderInLanguage("en", { read: momentView })).toBe("meals insulin on board 0.5 to 3 U at the event");
  });

  it("leaves the message of another sentence of the same words alone", async () => {
    expect(await renderInLanguage("de", { read: momentHeading })).toContain(
      "Mahlzeiten aktives Insulin (IOB) 0.5 bis 3 U beim Ereignis"
    );
  });

  it("lays the head of a When caption and its axis out as the message does, with the language's list words", async () => {
    const german = await renderInLanguage("de", { read: whenCaption });

    expect(german).toMatch(/^nach Uhrzeit: Wenn Mahlzeiten und .+ auftreten$/);
    expect(german).not.toContain(" and ");
    expect(await renderInLanguage("en", { read: whenCaption })).toMatch(/^When meals and .+ happen, by hour of day$/);
  });
});

describe("the clause of a caption under the production translation transform", () => {
  const layout = (withSignals: boolean) => () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("timeline");
    if (withSignals) ws.toggleSignal("glucose");
    return ws
      .caption()
      .map((p) => `${p.kind}[${p.before}]`)
      .join("|");
  };

  beforeAll(() => {
    seed("caption-clause", "{0} {1} {2}", [1, ", ", 0, " - ", 2]);
    seed("caption-clause-without-signals", "{0} {1}", [1, " / ", 0]);
  });

  it("places the signals, lens and range, and the text between them, as the message of the language does", async () => {
    expect(await renderInLanguage("de", { read: layout(true) })).toBe("lens[]|signals[, ]|range[ - ]");
    expect(await renderInLanguage("de", { read: layout(false) })).toBe("range[]|lens[ / ]");
  });

  it("keeps the English order and spaces", async () => {
    expect(await renderInLanguage("en", { read: layout(true) })).toBe("signals[]|lens[ ]|range[ ]");
    expect(await renderInLanguage("en", { read: layout(false) })).toBe("lens[]|range[ ]");
  });

  it("keeps every part, in the English order, when a translation drops one", async () => {
    seed("caption-clause", "{0} {1} {2}", [1, " ", 0]);

    expect(await renderInLanguage("de", { read: layout(true) })).toBe("signals[]|lens[ ]|range[ ]");
  });
});
