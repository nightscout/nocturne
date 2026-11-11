import { describe, expect, it } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Report } from "./report.svelte.js";
import savedBefore from "./saved-before-the-port.fixture.json";
import { BUILDER_VERSION, parseSavedReport } from "./saved-state.js";
import { STARTERS } from "./starters.js";
import { Workspace, vocabularyOf } from "./workspace.svelte.js";

const vocab = vocabularyOf(liveCatalogue);

describe("the starting questions against the catalogue", () => {
  it("name only ids the catalogue has", () => {
    const signals = new Set(vocab.signals.map((s) => s.id));
    const anchors = new Set(vocab.anchors.map((a) => a.id));
    const unknown: string[] = [];
    for (const s of STARTERS) {
      if (!vocab.lenses.includes(s.lens)) unknown.push(`${s.id}: lens ${s.lens}`);
      if (!vocab.rangePresets.some((p) => p.hours === s.rangeHours)) unknown.push(`${s.id}: range ${s.rangeHours}`);
      for (const sig of s.signals) {
        if (!signals.has(sig.id)) unknown.push(`${s.id}: signal ${sig.id}`);
        else if (!vocab.cellOf(s.lens, sig.id)) unknown.push(`${s.id}: ${s.lens} cannot draw ${sig.id}`);
      }
      for (const m of s.moments ?? []) if (!anchors.has(m.what)) unknown.push(`${s.id}: event ${m.what}`);
      for (const day of [...(s.dayGroups ?? []).flat(), ...(s.moments ?? []).flatMap((m) => m.weekdays ?? [])])
        if (!vocab.weekdays.includes(day)) unknown.push(`${s.id}: weekday ${day}`);
      if (s.dayFilter && !vocab.dayKinds.some((k) => k.id === s.dayFilter?.kind)) unknown.push(`${s.id}: day kind ${s.dayFilter.kind}`);
      const ev = s.event ?? {};
      if (ev.show && !vocab.shows.includes(ev.show)) unknown.push(`${s.id}: view ${ev.show}`);
      for (const o of [ev.up, ev.figure]) if (o && !vocab.outcomes.some((x) => x.id === o)) unknown.push(`${s.id}: figure ${o}`);
      if (ev.along && !vocab.alongs.includes(ev.along)) unknown.push(`${s.id}: along ${ev.along}`);
      if (ev.when && !vocab.whenAxes.includes(ev.when)) unknown.push(`${s.id}: when ${ev.when}`);
    }
    expect(unknown).toEqual([]);
  });

  it("each build a state the workspace keeps and reopens, and a spec", () => {
    for (const s of STARTERS) {
      const ws = new Workspace(liveCatalogue, s);
      expect(ws.toSpec(), s.id).not.toBeNull();
      const report = new Report(liveCatalogue, s);
      expect(parseSavedReport(BUILDER_VERSION, JSON.parse(JSON.stringify(report.toState())), vocab), s.id).not.toBeNull();
    }
  });
});

describe("a report saved before the vocabulary came from the catalogue", () => {
  it("reopens, version 1 as it was, and makes the spec it was saved with", () => {
    for (const [id, saved] of Object.entries(savedBefore)) {
      const reopened = Report.fromState(liveCatalogue, BUILDER_VERSION, saved.state);
      expect(reopened, id).not.toBeNull();
      expect(reopened!.spec(), id).toEqual(saved.spec);
    }
  });
});

describe("the catalogue's empty members", () => {
  it("offer no option a null member leaves out: start from zero is glucose around an event only", () => {
    const offered: string[] = [];
    for (const lens of vocab.lenses) {
      const ws = new Workspace(liveCatalogue);
      ws.setLens(lens);
      for (const s of vocab.signals) {
        if (!vocab.cellOf(lens, s.id)) continue;
        ws.toggleSignal(s.id);
        if (ws.relateOptions(s.id).fromStart) offered.push(`${lens}/${s.id}`);
        ws.toggleSignal(s.id);
      }
    }
    expect(offered).toEqual(vocab.ws.cells.filter((c) => (c.fromStartMarks ?? []).length > 0).map((c) => `${c.lens}/${c.signal}`));
    expect(offered.every((o) => o.startsWith("event/"))).toBe(true);
  });
});
