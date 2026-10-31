import { describe, expect, it } from "vitest";
import { catalogue, statisticsTemplate } from "./schema-form.fixtures.js";
import { SpecDraft } from "./spec-draft.svelte.js";

describe("the builder's draft of a spec", () => {
  it("lets a panel whose range is the report's take the report's, so a change to the report reaches it", () => {
    const draft = new SpecDraft(catalogue, statisticsTemplate.spec);

    expect(draft.range).toEqual({ hours: 336 });
    expect(draft.panels[0].range).toBeNull();
    draft.range = { hours: 48 };
    expect(draft.toSpec()).toMatchObject({ range: { hours: 48 }, panels: [{ id: "p1" }] });
    expect(draft.toSpec().panels).toEqual([expect.not.objectContaining({ range: expect.anything() })]);
  });

  it("sends only what was chosen, and starts a new panel with the catalogue's first readable dataset and its defaults", () => {
    const draft = new SpecDraft(catalogue);
    draft.addPanel();

    expect(draft.toSpec()).toEqual({
      version: 1,
      panels: [
        {
          id: "p1",
          title: "Glucose statistics",
          dataset: { name: "glucose_statistics" },
          primitive: "stat_tiles",
          bindings: { columns: ["timeInRangePercent", "mean", "tdd"] },
        },
      ],
    });
  });

  it("resets parameters and bindings on another dataset, keeps a primitive it allows, and names the panel after it", () => {
    const draft = new SpecDraft(catalogue);
    draft.addPanel();
    draft.setPrimitive(0, "table");
    draft.setParameters(0, { anything: 1 });
    draft.setBinding(0, "columns", ["mean"]);

    draft.setDataset(0, "every_shape");

    expect(draft.panels[0]).toMatchObject({ dataset: "every_shape", primitive: "table", parameters: {}, title: "Every setting" });
    expect(draft.panels[0].bindings.columns).toEqual(["timeInRangePercent", "mean", "tdd"]);
  });

  it("adds no panel past the most a report holds, and moves and removes panels", () => {
    const draft = new SpecDraft({ ...catalogue, maxPanels: 2 });
    draft.addPanel("glucose_statistics");
    draft.addPanel("every_shape");
    draft.addPanel("treatments");

    expect(draft.atPanelLimit).toBe(true);
    expect(draft.panels.map((p) => p.dataset)).toEqual(["glucose_statistics", "every_shape"]);
    draft.movePanel(1, -1);
    expect(draft.panels.map((p) => p.id)).toEqual(["p2", "p1"]);
    draft.removePanel(0);
    draft.addPanel("treatments");
    expect(draft.panels.map((p) => p.id)).toEqual(["p1", "p2"]);
  });
});
