import { describe, it, expect } from "vitest";
import { FACT_GROUP_ORDER, LEAF_FACTS } from "./factCatalog";
import { factDescription, factGroupLabel, factLabel } from "./factText.svelte";

const ACRONYMS = new Set(["AAPS", "CGM", "APS", "IOB", "COB", "DND", "Trio"]);

function isSentenceCase(text: string): boolean {
  return text
    .split(/\s+/)
    .slice(1)
    .every(
      (word) =>
        ACRONYMS.has(word.replace(/[^A-Za-z]/g, "")) ||
        !/^[A-Z][a-z]/.test(word)
    );
}

const texts = [
  ...LEAF_FACTS.flatMap((f) => [
    { id: `${f.kind} label`, text: factLabel(f.kind) },
    { id: `${f.kind} description`, text: factDescription(f.kind) },
  ]),
  ...FACT_GROUP_ORDER.map((g) => ({
    id: `${g} group`,
    text: factGroupLabel(g),
  })),
];

describe("factText", () => {
  it("never returns the raw kind", () => {
    for (const fact of LEAF_FACTS) {
      expect(factLabel(fact.kind)).not.toBe(fact.kind);
    }
  });

  it("starts every text with an uppercase letter so Wuchale extracts it", () => {
    for (const { id, text } of texts) {
      expect(text, id).toMatch(/^[A-Z]/);
    }
  });

  it("uses sentence case", () => {
    for (const { id, text } of texts) {
      expect(isSentenceCase(text), `${id}: ${text}`).toBe(true);
    }
  });

  it("does not repeat the label as the description", () => {
    for (const fact of LEAF_FACTS) {
      expect(factDescription(fact.kind)).not.toBe(factLabel(fact.kind));
    }
  });

  it("spells out abbreviations", () => {
    expect(factLabel("iob")).toBe("Insulin on board");
    expect(factLabel("cob")).toBe("Carbs on board");
  });
});
