import { describe, expect, it } from "vitest";
import { EventAnchorKind } from "#lib/api/index.js";
import type { CustomReportCatalogue } from "@nocturne/api-client";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Report } from "./report.svelte.js";
import { STARTERS, starter } from "./starters.js";
import * as words from "./vocabulary-words.svelte.js";
import {
  Workspace,
  comboLabel,
  comboPhrase,
  dayFilterPhrase,
  dayFilterText,
  leaveOutDefaults,
  sinceMealHeading,
  sinceMealText,
  vocabularyOf,
  windowPhrase,
  windowText,
} from "./workspace.svelte.js";

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
    expect(reason).toMatch(/too many signals and options/);
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
    const word = words.leaveOutPhrase(l.id);
    const said = new RegExp(`without [^,]*\\b${word}\\b`).test(text);
    if (!said) continue;
    const sent = json.includes(`"${l.parameter}":true`) || (defaults[l.id] && !json.includes(`"${l.parameter}":false`));
    out.push({ claim: `without ${word}`, holds: sent });
  }
  return out;
}

describe("the caption never claims what the spec does not send", () => {
  // One test per starter: each walks every lens and view in both orders.
  it.each(STARTERS.map((s) => [s.id, s] as const))("for %s, in any lens or view", (_, s) => {
    for (const lens of vocab.lenses)
      for (const show of vocab.shows)
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
  });
});

describe("small multiples", () => {
  it("heads each small chart with its group's letter and the member's words, and leaves the saved headings to the engine", () => {
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
    // The saved page heads each small chart with the engine's name for its group, so no panel takes
    // the chart's title.
    expect(titles).toEqual([undefined, undefined, undefined, undefined]);
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
    check("signal in a sentence", ws.signals.map((s) => s.id), words.signalPhrases());
    check("signal description", ws.signals.map((s) => s.id), words.signalDescriptions());
    check("section", [...ws.sections.map((s) => s.id), ...ws.signals.map((s) => s.section)], words.sectionLabels());
    check("signal group", [...ws.sections.flatMap((s) => s.groups), ...ws.signals.map((s) => s.group)], words.signalGroupLabels());
    check("search key", ws.signals.flatMap((s) => s.searchKeys), Object.fromEntries(Object.keys(words.searchTermWords()).map((k) => [k, k])));
    check("lens", ws.lenses, words.lensLabels());
    check("lens in a sentence", ws.lenses, words.lensPhrases());
    check("lens description", ws.lenses, words.lensDescriptions());
    check("family", ws.families.map((f) => f.id), words.familyLabels());
    check("kind of event", ws.anchors.map((a) => a.id), words.anchorLabels());
    check("kind of event at the start of a sentence", ws.anchors.map((a) => a.id), words.anchorHeadings());
    check("kind of event in a sentence", ws.anchors.map((a) => a.id), words.anchorPhrases());
    check("verb of a kind of event", ws.anchors.map((a) => a.id), words.anchorVerbs());
    check("group of kinds of event", ws.anchorGroups, words.anchorGroupLabels());
    check("day kind", ws.dayKinds.map((k) => k.id), words.dayKindLabels());
    check("day group preset", ws.dayGroupPresets.map((p) => p.id), words.dayGroupPresetLabels());
    check("leave out", ws.leaveOuts.map((l) => l.id), words.leaveOutLabels());
    check("leave out in a sentence", ws.leaveOuts.map((l) => l.id), words.leaveOutPhrases());
    check("range", ws.rangePresets.map((p) => p.id), words.rangeLabels());
    check("view", ws.events.shows.map((s) => s.id), words.eventShowLabels());
    check("figure", ws.events.outcomes.map((o) => o.id), words.outcomeLabels());
    check("figure in a sentence", ws.events.outcomes.map((o) => o.id), words.outcomePhrases());
    check("along", ws.events.along, words.alongLabels());
    check("along in a sentence", ws.events.along, words.alongPhrases());
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

describe("a name inside a sentence", () => {
  it("is its own message, so an abbreviation keeps its case", () => {
    const combo = { a: "glucose", b: "settingIsf", op: "difference" } as const;
    expect(comboLabel(combo)).toBe("Glucose minus ISF in data and profile");
    expect(comboPhrase(combo)).toBe("glucose minus ISF in data and profile");
  });
});

describe("a name at the start of a sentence and inside one", () => {
  it("heads a group of sleep starts and wake-ups in the plural, like the other kinds of event", () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("event");
    ws.addMoment(EventAnchorKind.SleepStart);
    ws.addMoment(EventAnchorKind.Wake);
    ws.updateMoment(0, { what: EventAnchorKind.SleepStart, timeOfDay: { fromMinute: 1320, toMinute: 1380 } });
    ws.updateMoment(1, { what: EventAnchorKind.Wake, timeOfDay: { fromMinute: 360, toMinute: 480 } });

    expect(ws.groupHeading(0)).toBe("A: Sleep starts 22:00 to 23:00");
    expect(ws.groupHeading(1)).toBe("B: Wake-ups 06:00 to 08:00");
  });

  it("says the time since the last carbs with a capital at the start and in lower case inside", () => {
    expect(sinceMealText({ min: 240 })).toBe("no carbs in the 4 hours before");
    expect(sinceMealHeading({ min: 240 })).toBe("No carbs in the 4 hours before");
    expect(sinceMealText({ max: 60 })).toBe("carbs in the hour before");
    expect(sinceMealHeading({ max: 60 })).toBe("Carbs in the hour before");
    expect(sinceMealText({ min: 30, max: 120 })).toBe("the last carbs 30 minutes to 2 hours before");
    expect(sinceMealHeading({ min: 30, max: 120 })).toBe("The last carbs 30 minutes to 2 hours before");
    expect(sinceMealHeading(undefined)).toBeNull();
  });

  it("says an event window with a capital at the start and in lower case inside", () => {
    expect(windowText(0, 300)).toBe("From the event to 5 hours after");
    expect(windowPhrase(0, 300)).toBe("from the event to 5 hours after");
    expect(windowText(60, 300)).toBe("1 hour before to 5 hours after");
    expect(windowPhrase(60, 300)).toBe("1 hour before to 5 hours after");
  });

  it("puts a window that is not the default inside the caption in lower case", () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("event");
    ws.toggleSignal("glucose");
    ws.setEvent({ before: 0, after: 120 });

    expect(ws.captionText()).toContain("around meals, from the event to 2 hours after");
    expect(ws.optionsSummary).toBe("from the event to 2 hours after, without illness and manual mode");
  });

  it("says what a day had with a capital at the start and in lower case inside", () => {
    const clock = (m: number) => `${m / 60}h`;
    const cases = [
      [{ kind: "exercise" }, "Days with exercise", "days with exercise"],
      [{ kind: "illness" }, "Days with illness", "days with illness"],
      [{ kind: "low" }, "Days with a low", "days with a low"],
      [{ kind: "lowOvernight" }, "Days with a low from 0h to 6h", "days with a low from 0h to 6h"],
      [{ kind: "carbs" }, "Days by total carbs", "days by total carbs"],
      [{ kind: "carbs", minCarbs: 100 }, "Days with at least 100 g of carbs", "days with at least 100 g of carbs"],
    ] as const;
    for (const [filter, heading, phrase] of cases) {
      expect(dayFilterText(filter, clock)).toBe(heading);
      expect(dayFilterPhrase(filter, clock)).toBe(phrase);
    }
  });

  it("says a kind of lens inside a sentence", () => {
    expect(words.lensPhrase("typical_day")).toBe("typical day");
    expect(words.lensPhrase("event")).toBe("around an event");
    expect(words.lensPhrase("against")).toBe("one against another");
  });

  it("says the range a block has chosen inside a sentence", () => {
    const report = new Report(liveCatalogue);
    report.add("table");
    const table = report.blocks[1];
    expect(report.blockRangePhrase(table)).toBe("same as the report");
    report.setBlockRange(table, 168);
    expect(report.blockRangePhrase(table)).toBe("7 days");
  });

  it("counts the figures of a block", () => {
    const report = new Report(liveCatalogue);
    report.add("figures");
    const figures = report.blocks[1];
    if (figures.kind !== "figures") throw new Error("figures");
    expect(report.caption(figures)).toMatch(/^Glucose statistics: \d+ figures over the last \d+ days$/);
    for (const column of report.columns(figures).filter((c) => c.on).slice(1)) report.toggleColumn(report.blocks[1] as typeof figures, column.field);
    expect(report.caption(report.blocks[1])).toMatch(/^Glucose statistics: 1 figure over the last \d+ days$/);
  });

  it("separates the groups and options of a caption as a list, and the clause before them by a space", () => {
    const ws = new Workspace(liveCatalogue, starter("exercise-no-carbs")!);
    const parts = ws.caption();

    expect(parts.map((p) => p.kind)).toEqual(["signals", "lens", "range", "groups", "options"]);
    expect(parts.map((p) => p.before)).toEqual(["", " ", " ", ", ", ", "]);
    expect(parts.map((p) => p.before + p.text).join("")).toBe(ws.captionText());
  });
});
