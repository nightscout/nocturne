import { describe, expect, it } from "vitest";
import { catalogue, chartTemplate, statisticsTemplate } from "./schema-form.fixtures.js";
import { SpecDraft } from "./spec-draft.svelte.js";

describe("the builder's draft of a spec", () => {
  it("lets a panel whose range is the report's take the report's, so a change to the report reaches it", () => {
    const draft = new SpecDraft(catalogue, statisticsTemplate.spec);

    expect(draft.range).toEqual({ hours: 336 });
    expect(draft.panels[0].range).toBeNull();
    draft.range = { hours: 48 };
    expect(draft.toSpec()).toMatchObject({ range: { hours: 48 }, panels: [{ id: "p1", kind: "stat_tiles" }] });
    expect(draft.toSpec().panels).toEqual([expect.not.objectContaining({ range: expect.anything() })]);
  });

  it("starts a new panel as a chart of one track of one layer of the first chart dataset the caller may read", () => {
    const draft = new SpecDraft(catalogue);
    draft.addPanel();

    expect(draft.toSpec()).toEqual({
      version: 1,
      panels: [
        {
          id: "p1",
          title: "Glucose readings",
          kind: "chart",
          tracks: [{ id: "t1", layers: [{ id: "l1", dataset: { name: "glucose_series" }, mark: "line", bindings: { x: "time", y: ["glucose"] } }] }],
        },
      ],
    });
  });

  it("reads a normalised chart, its tracks and layers, and writes it back without the nulls", () => {
    const draft = new SpecDraft(catalogue, chartTemplate.spec);

    expect(draft.panels[0].tracks.map((t) => t.layers.map((l) => l.id))).toEqual([["glucose"], ["iob"]]);
    const spec = draft.toSpec();
    expect(spec).toMatchObject({
      panels: [
        {
          kind: "chart",
          tracks: [
            { id: "glucose", height: 2, layers: [{ id: "glucose", mark: "line", bindings: { x: "time", y: ["glucose"] } }] },
            { id: "onboard", layers: [{ id: "iob" }] },
          ],
        },
      ],
    });
    expect(JSON.stringify(spec)).not.toContain("null");
  });

  it("adds, moves and removes tracks and layers, and adds no layer past the chart's most", () => {
    const draft = new SpecDraft({ ...catalogue, maxLayers: 3 }, chartTemplate.spec);
    draft.addTrack(0);
    expect(draft.panels[0].tracks.map((t) => t.id)).toEqual(["glucose", "onboard", "t3"]);
    expect(draft.panels[0].tracks[2].layers[0].id).toBe("l3");
    draft.addLayer(0, 2);
    expect(draft.atLayerLimit(draft.panels[0])).toBe(true);
    expect(draft.layerCount(draft.panels[0])).toBe(3);

    draft.moveTrack(0, 2, -1);
    expect(draft.panels[0].tracks.map((t) => t.id)).toEqual(["glucose", "t3", "onboard"]);
    draft.removeLayer(0, 0, 0);
    expect(draft.panels[0].tracks[0].layers, "a track keeps its last layer").toHaveLength(1);
    draft.removeTrack(0, 1);
    expect(draft.panels[0].tracks.map((t) => t.id)).toEqual(["glucose", "onboard"]);
  });

  it("derives a layer from the chart's first dataset layer, and copies a source from another layer", () => {
    const draft = new SpecDraft(catalogue, chartTemplate.spec);
    draft.setLayerOp(0, 1, 0, "scatter");

    const derived = draft.panels[0].tracks[1].layers[0].derived!;
    expect(derived.a).toEqual({ dataset: "glucose_series", parameters: {}, derived: null, field: "glucose" });
    expect(derived.b).toEqual(derived.a);
    expect(draft.panels[0].tracks[1].layers[0]).toMatchObject({ dataset: null, mark: "points", bindings: { x: "a", y: ["b"], fit: "fit" } });

    const iobLayer = { ...draft.panels[0].tracks[0].layers[0], bindings: { x: "time", y: ["iob"] } };
    draft.copySource(0, 1, 0, "a", iobLayer);
    expect(draft.panels[0].tracks[1].layers[0].derived!.a.field).toBe("iob");

    expect(draft.toSpec()).toMatchObject({
      panels: [{ tracks: [{}, { layers: [{ derived: { op: "scatter", a: { dataset: { name: "glucose_series" }, field: "iob" }, b: { field: "glucose" } } }] }] }],
    });
  });

  it("turns a chart into figures of its dataset where that dataset can be shown so, and back", () => {
    const draft = new SpecDraft(catalogue);
    draft.addPanel("glucose_statistics");
    draft.setKind(0, "stat_tiles");

    expect(draft.panels[0]).toMatchObject({ kind: "stat_tiles", dataset: "glucose_statistics", tracks: [] });
    draft.setKind(0, "chart");
    expect(draft.panels[0].tracks[0].layers[0].dataset).toBe("glucose_series");
  });

  it("adds no panel past the most a report holds, and moves and removes panels", () => {
    const draft = new SpecDraft({ ...catalogue, maxPanels: 2 });
    draft.addPanel("glucose_series");
    draft.addPanel("glucose_series");
    draft.addPanel("glucose_series");

    expect(draft.atPanelLimit).toBe(true);
    expect(draft.panels).toHaveLength(2);
    draft.movePanel(1, -1);
    expect(draft.panels.map((p) => p.id)).toEqual(["p2", "p1"]);
    draft.removePanel(0);
    draft.addPanel("glucose_series");
    expect(draft.panels.map((p) => p.id)).toEqual(["p1", "p2"]);
  });
});
