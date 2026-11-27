import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { AlertLimitFact, ForecastCurve } from "@nocturne/api-client";
import { seedCatalogue } from "#lib/test-stubs/catalogue-seed.js";
import { renderInLanguage } from "#lib/test-fixtures/render-in-language.js";
import { limitFactWords } from "./alert-labels.svelte.js";
import { heldReason } from "./holdText.svelte.js";
import OperatorToggle from "./OperatorToggle.svelte";
import RuleBuilderLeafEditor from "./RuleBuilderLeafEditor.svelte";

let restore: () => void;

beforeAll(async () => {
  restore = await seedCatalogue([
    { text: "one curve", de: "eine Kurve" },
    { text: "lowest of", de: "niedrigster von" },
    { text: "highest of", de: "hoechster von" },
    { text: "at most", de: "hoechstens" },
    { text: "more than", de: "mehr als" },
    { text: "all of", context: "operator-toggle", de: "alle von" },
    { text: "any", context: "operator-toggle", de: "irgendeine" },
    { text: "target low", de: "Zielwert niedrig" },
    { text: "too old", de: "zu alt" },
    { text: "has a time in the future", de: "hat eine Zeit in der Zukunft" },
  ]);
}, 120_000);

afterAll(() => restore());

const editor = (predicted: object) => () =>
  renderInLanguage("de", {
    component: RuleBuilderLeafEditor,
    viewProps: { node: { type: "predicted", predicted: { operator: "<=", value: 70, within_minutes: 30, ...predicted } } },
  });

describe("labels the alert editor builds in script, under the production translation transform", () => {
  it("translates a curve choice and a comparison that start with a lowercase letter", async () => {
    const text = await editor({ min_of: [ForecastCurve.Iob, ForecastCurve.Zt] })();

    expect(text).toContain("niedrigster von");
    expect(text).toContain("hoechstens");
    expect(text).not.toContain("lowest of");
    expect(text).not.toContain("at most");
  });

  it("translates the other curve choice", async () => {
    const text = await editor({ curve: ForecastCurve.Iob, operator: ">" })();

    expect(text).toContain("eine Kurve");
    expect(text).toContain("mehr als");
  });

  it("translates the words that name a fact a limit uses", async () => {
    expect(await renderInLanguage("de", { read: () => limitFactWords(AlertLimitFact.TargetLow) })).toBe("Zielwert niedrig");
  });

  it("translates the reasons a held rule gives", async () => {
    expect(await renderInLanguage("de", { read: () => heldReason("stale") })).toBe("zu alt");
    expect(await renderInLanguage("de", { read: () => heldReason("ahead_of_clock") })).toBe("hat eine Zeit in der Zukunft");
  });

  it("translates the operator toggle's default words, and leaves the size it compares with alone", async () => {
    const toggle = (size: "default" | "compact") =>
      renderInLanguage("de", { component: OperatorToggle, viewProps: { value: "and", size, onChange: () => {} } });

    expect(await toggle("default")).toContain("alle von");
    expect(await toggle("compact")).toContain("irgendeine");
  });
});
