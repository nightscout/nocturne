import { describe, expect, it } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Report } from "./report.svelte.js";
import { STARTERS, starter } from "./starters.js";
import { Workspace, fromDisplay } from "./workspace.svelte.js";
import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";

/** The live catalogue as a tenant that reads glucose in mmol/L sees it. */
function mmolCatalogue(): CustomReportCatalogue {
  const copy: CustomReportCatalogue = structuredClone(liveCatalogue);
  for (const d of copy.datasets)
    for (const f of d.fields) if (f.unit === "mg/dL") f.unit = "mmol/L";
  return copy;
}

function from(id: string): Workspace {
  return new Workspace(liveCatalogue, starter(id)!);
}

type Spec = {
  range: { hours: number };
  panels: {
    id: string;
    kind?: string;
    grid?: number;
    range?: { hours: number };
    dataset?: Record<string, unknown>;
    bindings?: { columns?: string[] };
    tracks: {
      id: string;
      height?: number;
      layers: Record<string, unknown>[];
    }[];
  }[];
};

function spec(ws: Workspace): Spec {
  return ws.toSpec() as unknown as Spec;
}

function layerIds(ws: Workspace): string[][] {
  return spec(ws).panels[0].tracks.map((t) =>
    t.layers.map((l) => String(l.id))
  );
}

describe("the starters", () => {
  it("draws the last 24 hours in four lanes, the modes as one layer of spans", () => {
    const ws = from("last-24-hours");

    expect(spec(ws).range).toEqual({ hours: 24 });
    expect(spec(ws).panels[0].tracks.map((t) => [t.id, t.height])).toEqual([
      ["lane-glucose", 2],
      ["lane-insulin", 0.75],
      ["lane-food", 0.75],
      ["lane-context", 0.5],
    ]);
    expect(layerIds(ws)).toEqual([
      ["glucose", "target"],
      ["iob", "basal", "boluses"],
      ["cob", "carbs"],
      ["spans"],
    ]);
    expect(ws.built).toMatchObject({ tracks: 4, layers: 8 });
    expect(ws.built.reads).toHaveLength(5);
    expect(spec(ws).panels[0].tracks[3].layers[0]).toMatchObject({
      dataset: { name: "state_spans", kinds: ["pump_mode", "override"] },
      mark: "spans",
    });
    expect(ws.layersOf("overrides")).toEqual(["spans"]);
  });

  it("draws a later breakfast as one event layer with a cohort for each time of day", () => {
    const ws = from("later-breakfast");
    const layer = spec(ws).panels[0].tracks[0].layers[0];

    expect(layer).toMatchObject({
      id: "glucose",
      mark: "band",
      dataset: {
        name: "event_response",
        cohorts: [
          { anchor: "Meal", timeOfDay: { fromMinute: 360, toMinute: 510 } },
          { anchor: "Meal", timeOfDay: { fromMinute: 510, toMinute: 660 } },
        ],
        minutesBefore: 60,
        minutesAfter: 300,
      },
      bindings: { x: "minute", y: ["glucoseP50"], series: "series" },
    });
    expect(ws.momentViews.map((m) => m.cohorts)).toEqual([
      [{ layer: "glucose", cohort: 1 }],
      [{ layer: "glucose", cohort: 2 }],
    ]);
  });

  it("sends everything else as the API's group of all others", () => {
    const ws = from("one-food");
    const layer = spec(ws).panels[0].tracks[0].layers[0];

    expect((layer.dataset as { cohorts: unknown[] }).cohorts).toEqual([
      { anchor: "Meal" },
      { anchor: "Meal", others: true },
    ]);
    expect(ws.momentViews.map((m) => m.cohorts)).toEqual([
      [{ layer: "glucose", cohort: 1 }],
      [{ layer: "glucose", cohort: 2 }],
    ]);
    expect(layer.bindings).toMatchObject({ y: ["deltaP50"] });
    expect(ws.notes).toContain(
      "Pick a food. Until then, the group shows all meals."
    );
  });

  it("offers everything else only beside a group that narrows its events", () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("event");
    ws.toggleSignal("glucose");

    expect(ws.everythingElseApplies).toBe(false);
    ws.updateMoment(0, { minutesSinceMeal: { min: 240 } });
    expect(ws.everythingElseApplies).toBe(true);
    ws.addEverythingElse();
    expect(ws.everythingElseApplies).toBe(false);
    expect(
      (spec(ws).panels[0].tracks[0].layers[0].dataset as { cohorts: unknown[] })
        .cohorts
    ).toEqual([
      { anchor: "Meal", minutesSinceMeal: { min: 240 } },
      { anchor: "Meal", others: true },
    ]);
  });

  it("sends the conditions at the event, and says them in the caption", () => {
    const ws = from("exercise-no-carbs");

    expect(
      (spec(ws).panels[0].tracks[0].layers[0].dataset as { cohorts: unknown[] })
        .cohorts
    ).toEqual([
      { anchor: "ExerciseStart", minutesSinceMeal: { min: 240 } },
      { anchor: "ExerciseStart", others: true },
    ]);
    expect(ws.captionText()).toBe(
      "Glucose around exercise over the last 90 days, no carbs in the 4 hours before against everything else, without illness and manual mode"
    );
    ws.updateMoment(0, { insulinOnBoard: { min: 0.5, max: 3 } });
    expect(ws.momentViews[0].text).toBe(
      "no carbs in the 4 hours before, insulin on board 0.5 to 3 U at the event"
    );
  });

  it("sends glucose at the event in whole mg/dL, and shows it in the tenant's unit", () => {
    const glucoseOf = (ws: Workspace) =>
      (
        spec(ws).panels[0].tracks[0].layers[0].dataset as {
          cohorts: { startGlucose?: unknown }[];
        }
      ).cohorts.map((c) => c.startGlucose);

    const mgdl = from("meals-start-high");
    expect(mgdl.glucoseUnit).toBe("mg/dL");
    expect(glucoseOf(mgdl)).toEqual([{ min: 70, max: 180 }, { min: 181 }]);
    expect(mgdl.momentViews[1].text).toBe("glucose at least 181 mg/dL at the event");

    const mmol = new Workspace(mmolCatalogue(), starter("meals-start-high")!);
    expect(mmol.glucoseUnit).toBe("mmol/L");
    expect(glucoseOf(mmol)).toEqual([{ min: 70, max: 180 }, { min: 181 }]);
    expect(mmol.momentViews[0].text).toBe("glucose 3.9 to 10 mmol/L at the event");
    mmol.updateMoment(1, { startGlucose: fromDisplay({ min: 13.9 }, mmol.glucoseUnits) });
    expect(glucoseOf(mmol)[1]).toEqual({ min: 250 });
  });

  it("knows when the chart draws more than one group of events", () => {
    const ws = from("before-a-low");

    expect(ws.groupsApart).toBe(false);
    ws.addMoment();
    expect(ws.groupsApart).toBe(true);
  });

  it("starts the window 4 hours before each low, and gives the API's rule for lows", () => {
    const ws = from("before-a-low");

    expect(spec(ws).panels[0].tracks[0].layers[0].dataset).toMatchObject({
      cohorts: [{ anchor: "LowStart" }],
      minutesBefore: 240,
      minutesAfter: 120,
    });
    expect(ws.beforeChoices).toContain(240);
    expect(ws.anchorRule("LowStart")).toMatch(/^A low starts after at least 15 minutes/);
    expect(ws.anchorRule("Meal")).toBeNull();
    expect(ws.conditionReason("minutesSinceMeal")).toBeNull();
    expect(ws.conditionLimits("startGlucose")).toEqual({ min: 0, max: 600 });
  });

  it("draws the weekdays and the weekend as one band for each group of days", () => {
    const ws = from("weekdays-weekend");
    const layers = spec(ws).panels[0].tracks[0].layers;

    expect(
      layers.map((l) => [l.id, (l.dataset as { dayOfWeek: string[] }).dayOfWeek])
    ).toEqual([
      ["glucose-1", ["Mon", "Tue", "Wed", "Thu", "Fri"]],
      ["glucose-2", ["Sat", "Sun"]],
    ]);
    expect(ws.dayGroupLayersOf(1)).toEqual(["glucose-2"]);
  });

  it("draws insulin against glucose as a scatter and the lag as a second panel", () => {
    const ws = from("insulin-glucose");
    const panels = spec(ws).panels;

    expect(panels.map((p) => [p.id, p.grid])).toEqual([
      ["chart", 15],
      ["lag", 15],
    ]);
    expect(ws.layersOf("iob")).toEqual(["scatter", "lag"]);
  });

  it("puts the three settings on two tracks, as a track has at most two units", () => {
    const ws = from("settings-data");

    expect(layerIds(ws)).toEqual([
      ["settingBasal", "settingIsf"],
      ["settingCarbRatio"],
    ]);
    expect(spec(ws).panels[0].tracks[1].id).toBe("lane-settings-2");
    expect(spec(ws).panels[0].tracks[0].layers[1]).toMatchObject({
      dataset: { name: "settings_evidence", setting: "isf" },
      bindings: { x: "startMinute", y: ["isfObserved", "isfProfile"] },
    });
  });

  it("smooths time in range over 7 days with a trend, day by day", () => {
    const ws = from("time-in-range-weekly");

    expect(layerIds(ws)).toEqual([
      ["timeInRange", "timeInRange-smooth", "timeInRange-trend"],
    ]);
    expect(spec(ws).panels[0].grid).toBeUndefined();
  });

  it("sends the window of a starter that sets one", () => {
    const ws = from("sensor-first-day");

    expect(spec(ws).panels[0].tracks[0].layers[0].dataset).toMatchObject({
      cohorts: [{ anchor: "SensorStart" }],
      minutesBefore: 60,
      minutesAfter: 480,
    });
  });

  it("gives every starter a spec within the catalogue's limits", () => {
    for (const s of STARTERS) {
      const ws = new Workspace(liveCatalogue, s);
      expect(ws.toSpec(), s.id).not.toBeNull();
      expect(ws.built.layers, s.id).toBeLessThanOrEqual(liveCatalogue.maxLayers);
      expect(ws.built.tracks, s.id).toBeLessThanOrEqual(liveCatalogue.maxTracks);
    }
  });
});

describe("the events one by one", () => {
  it("draws each event as points, one layer for each group, with the line through them", () => {
    const ws = from("later-breakfast");
    expect(ws.setShow("each")).toBeNull();
    const layers = spec(ws).panels[0].tracks[0].layers;

    expect(layers).toHaveLength(2);
    expect(layers[1]).toMatchObject({
      id: "each-2",
      mark: "points",
      dataset: { name: "event_outcomes", group: 2, minutesBefore: 60 },
      bindings: { x: "startGlucose", y: ["peakRise"], fit: "peakRiseFit" },
    });
    expect(layers[0].dataset).not.toHaveProperty("comparisonRange");
    ws.setEvent({ fit: false });
    expect(spec(ws).panels[0].tracks[0].layers[0].bindings).not.toHaveProperty("fit");
    expect(ws.captionText()).toBe(
      "Peak rise against glucose at the event, for meals 06:00 to 08:30 against meals 08:30 to 11:00, over the last 28 days, without illness and manual mode"
    );
  });

  it("offers fall figures for lows and rise figures for meals", () => {
    const lows = from("how-far-lows-go");
    expect(lows.upChoices.map((o) => o.id)).toEqual([
      "nadir",
      "drop",
      "minutesToNadir",
      "glucoseAt2Hours",
      "netInsulin",
    ]);
    expect(lows.alongChoices.map((a) => a.id)).not.toContain("grams");
    expect(spec(lows).panels[0].tracks[0].layers[0].bindings).toMatchObject({
      x: "iobAtEvent",
      y: ["nadir"],
    });
    const meals = from("start-and-peak");
    expect(meals.upChoices.map((o) => o.id)).toContain("gramsPerUnit");
    expect(meals.upChoices.map((o) => o.id)).not.toContain("nadir");
  });

  it("counts a figure in ranges, and the events by hour or weekday, a bar for each group", () => {
    const spread = from("meal-rise-spread");
    expect(spec(spread).panels[0].tracks[0].layers[0]).toMatchObject({
      dataset: {
        name: "event_outcome_spread",
        outcome: "peakRise",
        cohorts: [
          { anchor: "Meal", minutesSinceMeal: { min: 240 } },
          { anchor: "Meal", others: true },
        ],
      },
      mark: "bars",
      bindings: { x: "range", y: ["events1", "events2"] },
    });
    expect(spread.captionText()).toBe(
      "How many meals had each peak rise, for meals no carbs in the 4 hours before against all other meals, over the last 90 days, without illness and manual mode"
    );
    const when = from("when-lows-highs");
    expect(spec(when).panels[0].tracks[0].layers[0]).toMatchObject({
      dataset: { name: "event_occurrence" },
      bindings: { x: "hour", y: ["events1", "events2"] },
    });
    expect(when.captionText()).toBe(
      "When lows and highs start, by hour of day, over the last 90 days, without illness and manual mode"
    );
  });

  it("refuses the event views beside an earlier period, with the reason", () => {
    const ws = from("later-breakfast");
    ws.setEvent({ comparison: { from: "2026-08-01", to: "2026-08-31" } });

    expect(ws.showReason("each")).toBe(
      "Remove the earlier period first. This shows the events of the range only."
    );
    expect(ws.showReason("curve")).toBeNull();
  });
});

describe("this round's engine features", () => {
  it("draws days with a low against all other days, two layers for each signal", () => {
    const ws = from("days-with-a-low");
    const layers = spec(ws).panels[0].tracks[0].layers;

    expect(layers.map((l) => [l.id, (l.dataset as { days: unknown }).days])).toEqual([
      ["glucose-1", { kind: "low" }],
      ["glucose-2", { kind: "low", others: true }],
    ]);
    expect(ws.dayGroupLayersOf(1)).toEqual(["glucose-2"]);
    expect(ws.captionText()).toBe(
      "Glucose over a typical day over the last 28 days, days with a low against all other days, without illness and manual mode"
    );
    ws.setLens("daily");
    ws.toggleSignal("timeInRange");
    expect(spec(ws).panels[0].tracks[0].layers.map((l) => l.id)).toEqual([
      "timeInRange-1",
      "timeInRange-2",
    ]);
  });

  it("draws four groups as one small chart each, titled by letter", () => {
    const ws = from("later-breakfast");
    ws.addMoment("LowStart");
    ws.addMoment("HighStart");

    expect(ws.smallMultiples).toBe(true);
    expect(ws.groupsApart).toBe(false);
    const panels = spec(ws).panels;
    expect(panels.map((p) => p.id)).toEqual(["chart-1", "chart-2", "chart-3", "chart-4"]);
    expect((panels[2] as { title?: string }).title).toBe("C: all lows");
    expect(panels[3].tracks[0].layers[0]).toMatchObject({
      id: "glucose-g4",
      dataset: { cohorts: [{ anchor: "HighStart" }] },
    });
    expect(ws.cohortsOf(3)).toEqual([{ layer: "glucose-g4", cohort: 1 }]);
    expect(ws.everythingElseReason).toBe(
      "Everything else can go with at most two other groups."
    );
  });

  it("takes the figures for each anchor from the catalogue", () => {
    const ws = from("low-treated");
    ws.setShow("each");

    expect(ws.upChoices.map((o) => o.id)).toEqual([
      "peakRise",
      "minutesToPeak",
      "minutesBackToStart",
      "glucoseAt2Hours",
      "netInsulin",
    ]);
    expect(spec(ws).panels[0].tracks[0].layers[0]).toMatchObject({
      title: "A: all carbs taken in a low",
    });
    expect(ws.anchorNote("SiteChange")).toBe("A site, cannula or pod change");
    expect(ws.anchorReason("SleepStart")).toBeNull();
  });

  it("draws heart rate, steps and sleep, each where it applies", () => {
    const tl = from("heart-rate-glucose");
    expect(spec(tl).panels[0].tracks.map((t) => t.id)).toContain("lane-body");
    tl.setMark("heartRate", "band");
    expect(
      spec(tl).panels[0].tracks.find((t) => t.id === "lane-body")!.layers[0]
    ).toMatchObject({ bindings: { edges: ["bpmMin", "bpmMax"] } });
    const night = from("sleep-next-day");
    night.setMark("sleep", "bars");
    expect(spec(night).panels[0].tracks.flatMap((t) => t.layers).find((l) => l.id === "sleep")).toMatchObject({
      dataset: { name: "sleep" },
      bindings: { x: "night", y: ["totalSleep"] },
    });
    const timeline = new Workspace(liveCatalogue);
    timeline.toggleSignal("sleep");
    expect(spec(timeline).panels[0].tracks[0].id).toBe("lane-context");
  });
});

describe("answers before the click", () => {
  it("lists only the lens's signals, and the others under the lenses that draw them", () => {
    const ws = from("last-24-hours");
    ws.setLens("typical_day");

    expect(ws.signals.map((s) => s.id)).not.toContain("boluses");
    expect(ws.elsewhere).toContainEqual({
      id: "boluses",
      lens: "timeline",
      placed: true,
      reason: null,
    });
    expect(ws.elsewhere.filter((e) => e.id === "readings").map((e) => e.lens)).toEqual([]);
    expect(ws.signals.find((s) => s.id === "readings")).toMatchObject({
      placed: false,
    });
  });

  it("disables a third signal under one against another, with the reason", () => {
    const ws = from("insulin-glucose");

    expect(ws.signals.find((s) => s.id === "cob")?.reason).toBe(
      "This view shows two signals. Remove one first."
    );
    expect(ws.toggleSignal("cob")).toBe(
      "This view shows two signals. Remove one first."
    );
    expect(ws.againstPair).toEqual(["iob", "glucose"]);
  });

  it("gives a signal whose data cannot cover the range a reason before it is placed", () => {
    const ws = new Workspace(liveCatalogue);
    ws.setRange(2160);
    ws.toggleSignal("glucose");

    expect(ws.signals.find((s) => s.id === "boluses")?.reason).toBe(
      "Boluses shows at most 14 days. Choose a shorter range first."
    );
    ws.setRange(336);
    expect(ws.signals.find((s) => s.id === "boluses")?.reason).toBeNull();
    ws.toggleSignal("boluses");
    expect(ws.ranges.find((r) => r.id === "90d")?.reason).toBe(
      "Boluses shows at most 14 days. Choose a shorter range first."
    );
  });

  it("gives a ninth layer its reason on the option, and keeps the chart", () => {
    const ws = from("last-24-hours");

    expect(ws.relateReason("iob", { trend: true })).toBe(
      "A chart draws at most 8 layers. Remove a signal or an option first."
    );
    expect(ws.setRelate("iob", { trend: true })).not.toBeNull();
    expect(ws.relate("iob").trend).toBe(false);
  });

  it("gives a fifth lane its reason before the switch is used", () => {
    const ws = from("last-24-hours");

    expect(ws.ownLaneReason("iob", true)).toBe(
      "A chart has at most 4 lanes. Remove a signal first."
    );
    expect(ws.canOwnLane("pumpModes")).toBe(false);
  });

  it("places a signal from another view by switching to it", () => {
    const ws = from("last-24-hours");

    expect(ws.placeIn("readings", "daily")).toBeNull();
    expect(ws.lens).toBe("daily");
    expect(ws.shown).toEqual(["readings", "carbs"]);
  });
});

describe("relate", () => {
  it("smooths a timeline signal on a grid the window fits at any range", () => {
    const ws = new Workspace(liveCatalogue);
    ws.toggleSignal("glucose");
    ws.setRange(672);
    ws.setRelate("glucose", { smooth: 60 });

    expect(spec(ws).panels[0].grid).toBe(30);
    expect(spec(ws).panels[0].tracks[0].layers[1]).toEqual({
      id: "glucose-smooth",
      derived: {
        op: "rolling_mean",
        minutes: 60,
        a: { dataset: { name: "glucose_series" }, field: "glucose" },
      },
      mark: "line",
    });
  });

  it("draws the mean and standard deviation and a trend with its interval as bands", () => {
    const ws = new Workspace(liveCatalogue);
    ws.toggleSignal("glucose");
    ws.setRange(168);
    ws.setRelate("glucose", { sd: 60, trend: true, confidence: true });

    expect(spec(ws).panels[0].tracks[0].layers.slice(1)).toMatchObject([
      { id: "glucose-sd", derived: { op: "band_sd", minutes: 60 }, mark: "band" },
      {
        id: "glucose-trend",
        derived: { op: "trend", confidence: true },
        mark: "band",
      },
    ]);
    ws.setRelate("glucose", { trend: false });
    expect(ws.relate("glucose").confidence).toBe(false);
  });

  it("subtracts one signal from another in a lane of its own, and names both", () => {
    const ws = new Workspace(liveCatalogue);
    ws.setRange(168);
    ws.toggleSignal("basal");
    ws.toggleSignal("iob");

    expect(ws.partnersOf("basal")).toEqual(["iob"]);
    expect(ws.combineReason("basal", "iob", "difference")).toBe(
      "Subtract takes two signals in one unit."
    );
    expect(ws.combine("iob", "basal", "ratio")).toBeNull();
    const track = spec(ws).panels[0].tracks.at(-1)!;
    expect(track.layers[0]).toMatchObject({
      title: "Insulin on board divided by basal delivered",
      derived: { op: "ratio" },
      mark: "line",
    });
    expect(ws.captionText()).toBe(
      "Basal delivered, insulin on board and insulin on board divided by basal delivered as a timeline over the last 7 days"
    );
    ws.toggleSignal("basal");
    expect(ws.combos).toEqual([]);
  });
});

describe("options of the event and typical day lenses", () => {
  it("leaves out only what differs from the API's defaults, and says so in the caption", () => {
    const ws = from("weekdays-weekend");
    ws.setLeaveOut("exercise", true);
    ws.setLeaveOut("illness", false);
    ws.setProfileName("Weekend");

    expect(spec(ws).panels[0].tracks[0].layers[0].dataset).toMatchObject({
      excludeExercise: true,
      excludeIllness: false,
      profileName: "Weekend",
    });
    expect(spec(ws).panels[0].tracks[0].layers[0].dataset).not.toHaveProperty(
      "excludeOpenLoop"
    );
    expect(ws.captionText()).toBe(
      "Glucose over a typical day over the last 28 days, Monday to Friday against Saturday and Sunday, without exercise and manual mode, only while Weekend was active"
    );
  });

  it("sends corroboration only with a group of rises", () => {
    const ws = from("later-breakfast");
    ws.setEvent({ requireCorroboration: true, isolatedOnly: true });

    expect(spec(ws).panels[0].tracks[0].layers[0].dataset).not.toHaveProperty(
      "requireCorroboration"
    );
    ws.updateMoment(0, { what: "UnannouncedRise" });
    expect(spec(ws).panels[0].tracks[0].layers[0].dataset).toMatchObject({
      requireCorroboration: true,
      isolatedOnly: true,
    });
  });

  it("does not offer leave out where no shown signal takes it", () => {
    const ws = new Workspace(liveCatalogue);
    ws.setLens("typical_day");
    ws.toggleSignal("mean");

    expect(ws.leaveOutApplies).toBe(false);
    ws.toggleSignal("glucose");
    expect(ws.leaveOutMisses).toEqual(["mean"]);
  });
});

describe("the caption", () => {
  const text = (id: string) => from(id).captionText();

  it("reads each starter as one sentence", () => {
    expect(text("later-breakfast")).toBe(
      "Glucose around meals over the last 28 days, 06:00 to 08:30 against 08:30 to 11:00, without illness and manual mode"
    );
    expect(text("insulin-glucose")).toBe(
      "Glucose against insulin on board each 15 minutes, with the lag between them over the last 14 days"
    );
    expect(text("sensor-first-day")).toBe(
      "Glucose around sensor starts, 1 hour before to 8 hours after over the last 90 days, without illness and manual mode"
    );
    expect(text("reservoir-batteries")).toBe(
      "Reservoir, pump battery and uploader battery as a timeline over the last 7 days"
    );
  });
});

describe("the report", () => {
  it("starts with one open chart on the starter's range, as the report's", () => {
    const report = new Report(liveCatalogue, starter("last-24-hours")!);
    const [chart] = report.blocks;

    expect(report.rangeHours).toBe(24);
    expect(report.open).toBe(chart.key);
    expect(report.spec()).toMatchObject({
      title: "The last 24 hours",
      range: { hours: 24 },
      panels: [{ id: `b${chart.key}-chart` }],
    });
    expect((report.spec() as unknown as Spec).panels[0].range).toBeUndefined();
  });

  it("gives a new block a plain title from its source, which follows the source until changed", () => {
    const report = new Report(liveCatalogue);
    report.add("table");
    const table = report.blocks[1];
    if (table.kind !== "table") throw new Error("table");

    expect(table.title).toBe("Treatments");
    report.setSource(table, "daily_summary");
    expect(report.blocks[1]).toMatchObject({ title: "Each day" });
    report.setBlockTitle(report.blocks[1], "My days");
    const renamed = report.blocks[1];
    if (renamed.kind !== "table") throw new Error("table");
    report.setSource(renamed, "treatments");
    expect(report.blocks[1]).toMatchObject({ title: "My days" });
  });

  it("adds a block of each kind, opens the new one, and keeps the report within its limits", () => {
    const report = new Report(liveCatalogue);
    for (const kind of ["figures", "table", "comparison"] as const)
      expect(report.add(kind)).toBeNull();

    expect(report.blocks.map((b) => b.kind)).toEqual([
      "chart",
      "figures",
      "table",
      "comparison",
    ]);
    expect(report.open).toBe(report.blocks[3].key);
    const panels = (report.spec() as unknown as Spec).panels;
    expect(panels.map((p) => p.kind)).toEqual(["stat_tiles", "table", "comparison"]);
    expect(panels[2].dataset).toEqual({
      name: "period_comparison",
      preset: "last14-prior14",
    });
    expect(report.addReason("comparison")).toBeNull();
    report.add("comparison");
    expect(report.add("table")).toBeNull();
    expect(report.addReason("figures")).toBe(
      "A report has at most 6 parts. The lag chart is a part of its own."
    );
  });

  it("refuses a third comparison before it is added", () => {
    const report = new Report(liveCatalogue);
    report.add("comparison");
    report.add("comparison");

    expect(report.addReason("comparison")).toBe(
      "A report has at most 2 comparisons."
    );
  });

  it("ticks the catalogue's figures and keeps at least one", () => {
    const report = new Report(liveCatalogue);
    report.add("figures");
    const block = report.blocks[1];
    if (block.kind !== "figures") throw new Error("figures");
    report.setSource(block, "device_status");

    const columns = report.columns(block);
    expect(columns.every((c) => c.on)).toBe(true);
    for (const c of columns.slice(1)) report.toggleColumn(block, c.field);
    expect(report.columns(block)[0].reason).toBe("Keep at least one figure.");
  });

  it("gives a block its own range, and the report's range a reason a chart cannot take", () => {
    const report = new Report(liveCatalogue, starter("last-24-hours")!);
    report.add("figures");
    const figures = report.blocks[1];
    report.setBlockRange(figures, 168);

    expect((report.spec() as unknown as Spec).panels[1].range).toEqual({
      hours: 168,
    });
    expect(report.ranges.find((r) => r.id === "90d")?.reason).toBe(
      "Basal delivered shows at most 14 days. Choose a shorter range first."
    );
  });

  it("moves and removes blocks", () => {
    const report = new Report(liveCatalogue);
    report.add("table");
    const [chart, table] = report.blocks;
    report.move(table.key, -1);

    expect(report.blocks.map((b) => b.key)).toEqual([table.key, chart.key]);
    report.remove(table.key);
    expect(report.blocks.map((b) => b.key)).toEqual([chart.key]);
    expect(report.open).toBe(chart.key);
  });
});
