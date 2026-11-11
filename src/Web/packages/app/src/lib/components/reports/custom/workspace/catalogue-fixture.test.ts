import { describe, expect, it } from "vitest";
import { CATALOGUE_MEMBERS, DATASET_MEMBERS, FIELD_MEMBERS, liveCatalogue } from "./catalogue.fixtures.js";

const required = (members: Record<string, boolean>): string[] =>
  Object.entries(members).flatMap(([k, needed]) => (needed ? [k] : []));

describe("the catalogue fixture", () => {
  it("has every member the generated client requires, so it has not drifted from the API", () => {
    const top = new Set<string>(Object.keys(liveCatalogue));
    expect(required(CATALOGUE_MEMBERS).filter((k) => !top.has(k))).toEqual([]);
    for (const dataset of liveCatalogue.datasets) {
      const keys = new Set<string>(Object.keys(dataset));
      expect(required(DATASET_MEMBERS).filter((k) => !keys.has(k)), dataset.name).toEqual([]);
      for (const field of dataset.fields) {
        const fieldKeys = new Set<string>(Object.keys(field));
        expect(required(FIELD_MEMBERS).filter((k) => !fieldKeys.has(k)), `${dataset.name}.${field.name}`).toEqual([]);
      }
    }
  });

  it("names no member the generated client does not declare", () => {
    const known = (members: Record<string, boolean>) => new Set<string>(Object.keys(members));
    expect(Object.keys(liveCatalogue).filter((k) => !known(CATALOGUE_MEMBERS).has(k))).toEqual([]);
    for (const dataset of liveCatalogue.datasets) {
      expect(Object.keys(dataset).filter((k) => !known(DATASET_MEMBERS).has(k)), dataset.name).toEqual([]);
      for (const field of dataset.fields)
        expect(Object.keys(field).filter((k) => !known(FIELD_MEMBERS).has(k)), field.name).toEqual([]);
    }
  });

  it("carries the event datasets' notes on each kind of event", () => {
    const anchors: unknown = liveCatalogue.datasets.find((d) => d.name === "event_response")?.parameters?.["x-eventAnchors"];
    expect(Object.keys(anchors ?? {})).toHaveLength(14);
  });
});
