import { describe, expect, it } from "vitest";
import { EventAnchorKind } from "#lib/api/index.js";
import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Report } from "./report.svelte.js";
import { STARTERS, starter } from "./starters.js";
import * as words from "./vocabulary-words.svelte.js";
import { Workspace, leaveOutDefaults, vocabularyOf } from "./workspace.svelte.js";

const vocab = vocabularyOf(liveCatalogue);

describe("the lens switch", () => {
  it("says why a lens cannot take the chart before it is used, and refuses it the same way", () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("typical_day");
    ws.toggleSignal("glucose");
    expect(ws.setDayGroupPreset("fridayAndWeekend")).toBeNull();
    ws.setLens("timeline");
    for (const id of ["target", "iob", "cob", "basal", "carbs", "boluses"] as const) ws.toggleSignal(id);

    const reason = ws.lensReason("typical_day");
    expect(reason).toMatch(/at most/);
    expect(ws.setLens("typical_day")).toBe(reason);
    expect(ws.lens).toBe("timeline");
    expect(ws.lensReason("timeline")).toBeNull();
  });
});

/** What the caption says the chart does, each checked against the spec the chart sends. */
function claims(ws: Workspace): { claim: string; holds: boolean }[] {
  const text = ws.captionText();
  const json = JSON.stringify(ws.toSpec() ?? {});
  const defaults = leaveOutDefaults(vocab);
  const out: { claim: string; holds: boolean }[] = [];
  if (ws.event.comparison && text.includes(ws.dates(ws.event.comparison)))
    out.push({ claim: "an earlier period", holds: json.includes('"comparisonRange"') });
  if (text.includes("only events with nothing else near them"))
    out.push({ claim: "isolated events", holds: json.includes('"isolatedOnly":true') });
  if (text.includes("only rises the loop answered"))
    out.push({ claim: "corroborated rises", holds: json.includes('"requireCorroboration":true') });
  for (const l of vocab.leaveOuts) {
    const word = words.leaveOutLabel(l.id).toLowerCase();
    const said = new RegExp(`without [^,]*\b${word}\b`).test(text);
    if (!said) continue;
    const sent = json.includes(`"${l.parameter}":true`) || (defaults[l.id] && !json.includes(`"${l.parameter}":false`));
    out.push({ claim: `without ${word}`, holds: sent });
  }
  return out;
}

describe("the caption", () => {
  it("never claims what the spec does not send, for any starter, lens or view", () => {
    for (const s of STARTERS)
      for (const lens of vocab.lenses)
        for (const show of vocab.shows) {
        for (const optionsFirst of [true, false]) {
          const ws = new Workspace(liveCatalogue, s);
          ws.setLens(lens);
          const options = () => {
            ws.setEvent({ isolatedOnly: true, requireCorroboration: true });
            ws.setEvent({ comparison: { from: "2026-08-01", to: "2026-08-14" } });
            ws.setLeaveOut("exercise", true);
          };
          if (optionsFirst) options();
          ws.setShow(show);
          if (!optionsFirst) options();
          // An empty chart shows no caption.
          if (ws.toSpec() === null) continue;
          for (const c of claims(ws)) expect(c.holds, `${s.id} ${lens} ${show}: ${c.claim}`).toBe(true);
        }
        }
  });
});

describe("small multiples", () => {
  it("heads each small chart with its group's letter and the member's words, and the chart's title once", () => {
    const report = new Report(liveCatalogue, starter("later-breakfast")!, (m) => `${m / 60}h`);
    const [chart] = report.blocks;
    if (chart.kind !== "chart") throw new Error("chart");
    chart.ws.addMoment(EventAnchorKind.LowStart);
    chart.ws.addMoment(EventAnchorKind.HighStart);
    report.setBlockTitle(chart, "Breakfasts");

    expect(chart.ws.smallMultiples).toBe(true);
    expect(chart.ws.groupHeading(0)).toBe("A: Meals 6h to 8.5h");
    expect(chart.ws.groupHeading(3)).toBe("D: All highs");
    const panels: unknown = report.spec().panels;
    const titles = (Array.isArray(panels) ? panels : []).map((p: { title?: string }) => p.title);
    expect(titles).toEqual(["Breakfasts", undefined, undefined, undefined]);
  });
});

describe("glucose in words", () => {
  it("writes glucose at the event in the tenant's unit in a table's caption too", () => {
    const mmol: CustomReportCatalogue = structuredClone(liveCatalogue);
    for (const d of mmol.datasets) for (const f of d.fields) if (f.unit === "mg/dL") f.unit = "mmol/L";
    const report = new Report(mmol);
    report.add("table");
    const table = report.blocks[1];
    if (table.kind !== "table") throw new Error("table");
    report.setSource(table, "event_response");
    table.events.updateMoment(0, { startGlucose: { min: 180 } });

    expect(report.caption(table)).toContain("10 mmol/L");
    expect(report.caption(table)).not.toContain("mg/dL");
  });
});

describe("the words of the vocabulary", () => {
  it("has a word for every id the catalogue sends", () => {
    const ws = vocab.ws;
    const missing: string[] = [];
    const check = (kind: string, ids: readonly string[], words: Record<string, string>) => {
      for (const id of ids) if (!Object.hasOwn(words, id)) missing.push(`${kind} ${id}`);
    };
    check("signal", ws.signals.map((s) => s.id), words.signalLabels());
    check("lens", ws.lenses, words.lensLabels());
    check("lens description", ws.lenses, words.lensDescriptions());
    check("family", ws.families.map((f) => f.id), words.familyLabels());
    check("kind of event", ws.anchors.map((a) => a.id), words.anchorLabels());
    check("kind of event in a sentence", ws.anchors.map((a) => a.id), words.anchorPhrases());
    check("verb of a kind of event", ws.anchors.map((a) => a.id), words.anchorVerbs());
    check("group of kinds of event", ws.anchorGroups, words.anchorGroupLabels());
    check("day kind", ws.dayKinds.map((k) => k.id), words.dayKindLabels());
    check("day group preset", ws.dayGroupPresets.map((p) => p.id), words.dayGroupPresetLabels());
    check("leave out", ws.leaveOuts.map((l) => l.id), words.leaveOutLabels());
    check("range", ws.rangePresets.map((p) => p.id), words.rangeLabels());
    check("view", ws.events.shows.map((s) => s.id), words.eventShowLabels());
    check("figure", ws.events.outcomes.map((o) => o.id), words.outcomeLabels());
    check("along", ws.events.along, words.alongLabels());
    check("when axis", ws.events.whenAxes, words.whenAxisLabels());
    check("when axis choice", ws.events.whenAxes, words.whenAxisChoices());
    check("mark", ws.cells.flatMap((c) => [...c.marks, ...(c.fromStartMarks ?? [])].map((m) => m.word)), words.markLabels());
    check("source", [...ws.sources.figures, ...ws.sources.tables], words.sourceLabels());
    check("scope", [...ws.cells.flatMap((c) => c.scopes), ...ws.anchors.flatMap((a) => a.scopes), ...ws.dayKinds.flatMap((k) => k.scopes)], words.scopeWords());
    check("starter", STARTERS.map((s) => s.id), Object.fromEntries(Object.entries(words.starterWords(String)).map(([id, w]) => [id, w.title])));
    check("starter group", STARTERS.map((s) => s.group), words.starterGroupLabels());
    expect([...new Set(missing)]).toEqual([]);
  });

  it("shows an id the web has no word for as the id itself", () => {
    expect(words.signalLabel("ketones")).toBe("ketones");
    expect(words.anchorLabel("Fasting")).toBe("Fasting");
  });
});

describe("the range a chart can take", () => {
  it("disables a preset a placed signal's data cannot cover, naming the signal", () => {
    const report = new Report(liveCatalogue, starter("last-24-hours")!);
    const [chart] = report.blocks;
    if (chart.kind !== "chart") throw new Error("chart");
    const reasons = Object.fromEntries(report.ranges.map((r) => [r.id, r.reason]));

    expect(reasons["14d"]).toBeNull();
    expect(reasons["28d"]).toMatch(/^(Insulin on board|Basal delivered|Boluses|Carbs|Modes|Overrides|[A-Z][a-z ,]+) shows at most 14 days\. Choose a shorter range first\.$/);
    expect(report.setRange(672)).toBe(reasons["28d"]);
    expect(report.rangeHours).toBe(24);
  });

  it("starts every question on a range its data covers", () => {
    for (const s of STARTERS) {
      const report = new Report(liveCatalogue, s);
      for (const block of report.blocks)
        if (block.kind === "chart") expect(block.ws.reportRangeReason(report.rangeHours), s.id).toBeNull();
    }
  });

  it("gives a count of readings no unit, so it takes no unit of a track's two", () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("typical_day");
    for (const id of ["glucose", "ranges", "readings"] as const) expect(ws.toggleSignal(id), id).toBeNull();
    const tracks = (ws.toSpec() as { panels: { tracks: { id: string }[] }[] }).panels[0].tracks;
    // Glucose and percent are the lane's two units; the count of readings adds none, so no second track.
    expect(tracks.map((t) => t.id)).toEqual(["lane-glucose"]);
  });
});
