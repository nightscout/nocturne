import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Report, type ChartBlock } from "./report.svelte.js";
import {
  DRAFT_MAX_AGE_MS,
  NEW_REPORT,
  ReportDraft,
  clearAllDrafts,
  clearDraft,
  readDraft,
  stateText,
  writeDraft,
  type DraftOwner,
} from "./report-draft.svelte.js";
import { clearMemberStorage } from "#lib/stores/member-storage.js";
import { BUILDER_VERSION } from "./saved-state.js";
import { starter } from "./starters.js";
import { dismissUndos, offerUndo, removeWithUndo } from "./undo.svelte.js";
import { spanRange } from "./date-span.js";
import { CUSTOM_RANGE, SAVED_RANGE, viewerRange } from "./viewer-range.svelte.js";

const toasts = vi.hoisted(() => ({ shown: [] as { message: string; undo: () => void }[], errors: [] as string[], dismissed: [] as number[] }));

vi.mock("svelte-sonner", () => ({
  toast: Object.assign(
    (message: string, options: { action: { onClick: () => void } }) => toasts.shown.push({ message, undo: options.action.onClick }),
    { error: (message: string) => toasts.errors.push(message), success: () => {}, dismiss: (id: number) => toasts.dismissed.push(id) }
  ),
}));

/** A browser storage held in a map, or one that refuses every call. */
function storage(refuses = false): Storage {
  const items = new Map<string, string>();
  const refuse = () => {
    throw new DOMException("The storage is not available.", "SecurityError");
  };
  return {
    get length() {
      return items.size;
    },
    key: (i) => [...items.keys()][i] ?? null,
    getItem: (k) => (refuses ? refuse() : (items.get(k) ?? null)),
    setItem: (k, v) => (refuses ? refuse() : void items.set(k, v)),
    removeItem: (k) => (refuses ? refuse() : void items.delete(k)),
    clear: () => items.clear(),
  };
}

const fromDraft = (d: { version: number; state: unknown }) => Report.fromState(liveCatalogue, d.version, d.state);

beforeEach(() => {
  vi.stubGlobal("localStorage", storage());
  toasts.shown.length = 0;
  toasts.errors.length = 0;
  toasts.dismissed.length = 0;
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the draft kept in this browser", () => {
  const me: DraftOwner = { tenant: "dev", subject: "subject-1" };
  const saved_at = "2026-11-16T01:00:00Z";

  it("keeps a report's state by its id and clears it", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    expect(writeDraft(me, "r1", stateText(report), saved_at, 5)).toBe(true);
    expect(readDraft(me, "r1")).toEqual({ version: BUILDER_VERSION, state: report.toState(), savedAt: saved_at, writtenAt: 5 });
    expect(readDraft(me, NEW_REPORT)).toBeNull();
    clearDraft(me, "r1");
    expect(readDraft(me, "r1")).toBeNull();
  });

  it("keeps each member's drafts apart, in each tenant", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    writeDraft(me, "r1", stateText(report), saved_at);
    expect(readDraft({ tenant: "dev", subject: "subject-2" }, "r1")).toBeNull();
    expect(readDraft({ tenant: "other", subject: "subject-1" }, "r1")).toBeNull();
    expect(localStorage.key(0)).toBe("nocturne.custom-report-draft.dev.subject-1.r1");
    expect(localStorage.length).toBe(1);
  });

  it("keeps nothing, throws nothing, and says so when browser storage refuses", () => {
    vi.stubGlobal("localStorage", storage(true));
    const draft = new ReportDraft("r1", me);
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    expect(writeDraft(me, "r1", stateText(report), saved_at)).toBe(false);
    expect(readDraft(me, "r1")).toBeNull();
    expect(draft.open(report, saved_at, fromDraft)).toBe(report);
    expect(draft.kept).toBe(true);
    report.title = "Changed";
    expect(() => draft.keep(report)).not.toThrow();
    expect(draft.dirty).toBe(true);
    expect(draft.kept).toBe(false);
    expect(() => clearAllDrafts()).not.toThrow();
  });

  it("reads a draft that is not JSON, not a state, or has no time of writing as none", () => {
    const key = "nocturne.custom-report-draft.dev.subject-1.r1";
    localStorage.setItem(key, "{not json");
    expect(readDraft(me, "r1")).toBeNull();
    localStorage.setItem(key, JSON.stringify({ state: {} }));
    expect(readDraft(me, "r1")).toBeNull();
    localStorage.setItem(key, JSON.stringify({ version: BUILDER_VERSION, state: {}, savedAt: null }));
    expect(readDraft(me, "r1")).toBeNull();
  });

  it("writes a draft only while the report differs from how it opened, and clears it after", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const draft = new ReportDraft("r1", me);
    const title = report.title;
    draft.begin(report);
    draft.keep(report);
    expect(draft.dirty).toBe(false);
    expect(readDraft(me, "r1")).toBeNull();

    report.title = "Mornings";
    draft.keep(report);
    expect(draft.dirty).toBe(true);
    expect(readDraft(me, "r1")?.state).toEqual(report.toState());

    report.title = title;
    draft.keep(report);
    expect(draft.dirty).toBe(false);
    expect(readDraft(me, "r1")).toBeNull();
  });

  it("opens a saved report with the changes kept for it, and with none once they are cleared", () => {
    const saved = new Report(liveCatalogue, starter("dawn-rise")!);
    const edited = Report.fromState(liveCatalogue, BUILDER_VERSION, saved.toState())!;
    edited.title = "Mornings, edited";
    writeDraft(me, "r1", stateText(edited), saved_at);

    const draft = new ReportDraft("r1", me);
    const shown = draft.open(saved, saved_at, fromDraft);
    expect(shown?.title).toBe("Mornings, edited");
    expect(draft.restored).toBe(true);
    expect(draft.dirty).toBe(true);
    expect(draft.stale).toBeNull();

    draft.clear();
    expect(readDraft(me, "r1")).toBeNull();
    expect(new ReportDraft("r1", me).open(saved, saved_at, fromDraft)).toBe(saved);
  });

  it("does not restore changes made to a version the server since replaced, until the member keeps them", () => {
    const saved = new Report(liveCatalogue, starter("dawn-rise")!);
    const edited = Report.fromState(liveCatalogue, BUILDER_VERSION, saved.toState())!;
    edited.title = "My old changes";
    writeDraft(me, "r1", stateText(edited), saved_at);
    const changed_elsewhere = Report.fromState(liveCatalogue, BUILDER_VERSION, saved.toState())!;
    changed_elsewhere.title = "Their new title";

    const draft = new ReportDraft("r1", me);
    expect(draft.open(changed_elsewhere, "2026-11-16T02:00:00Z", fromDraft)).toBe(changed_elsewhere);
    expect(draft.restored).toBe(false);
    expect(draft.dirty).toBe(false);
    expect(draft.stale?.title).toBe("My old changes");

    // While the member chooses, the saved report on screen does not overwrite the draft.
    draft.keep(changed_elsewhere);
    expect(readDraft(me, "r1")?.savedAt).toBe(saved_at);

    const kept = draft.keepStale();
    expect(kept?.title).toBe("My old changes");
    expect(draft.dirty).toBe(true);
    expect(draft.restored).toBe(true);
    // Kept, the changes belong to the version on the server, so the next open does not ask again.
    expect(readDraft(me, "r1")?.savedAt).toBe("2026-11-16T02:00:00Z");
  });

  it("drops changes made to a replaced version when the member discards them", () => {
    const saved = new Report(liveCatalogue, starter("dawn-rise")!);
    const edited = Report.fromState(liveCatalogue, BUILDER_VERSION, saved.toState())!;
    edited.title = "My old changes";
    writeDraft(me, "r1", stateText(edited), saved_at);
    const draft = new ReportDraft("r1", me);
    draft.open(saved, "2026-11-16T02:00:00Z", fromDraft);
    draft.discardStale();
    expect(draft.stale).toBeNull();
    expect(readDraft(me, "r1")).toBeNull();
    expect(new ReportDraft("r1", me).open(saved, "2026-11-16T02:00:00Z", fromDraft)).toBe(saved);
  });

  it("opens the report as saved, and drops the draft, when the draft no longer reads", () => {
    const saved = new Report(liveCatalogue, starter("dawn-rise")!);
    localStorage.setItem(
      "nocturne.custom-report-draft.dev.subject-1.r1",
      JSON.stringify({ version: BUILDER_VERSION + 1, state: {}, savedAt: saved_at, writtenAt: Date.now() })
    );
    const draft = new ReportDraft("r1", me);
    expect(draft.open(saved, saved_at, fromDraft)).toBe(saved);
    expect(draft.restored).toBe(false);
    expect(readDraft(me, "r1")).toBeNull();
  });

  it("opens a new report from its draft, which counts as unsaved", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    writeDraft(me, NEW_REPORT, stateText(report), null);
    const draft = new ReportDraft(NEW_REPORT, me);
    expect(draft.open(null, null, fromDraft)?.toState()).toEqual(report.toState());
    expect(draft.dirty).toBe(true);
  });

  it("drops drafts older than the longest age, and drafts written before drafts had an owner, on open", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const now = Date.now();
    writeDraft(me, "old", stateText(report), saved_at, now - DRAFT_MAX_AGE_MS - 1);
    writeDraft(me, "recent", stateText(report), saved_at, now - 1000);
    localStorage.setItem("nocturne.custom-report-draft.r9", JSON.stringify({ version: BUILDER_VERSION, state: {} }));
    new ReportDraft(NEW_REPORT, me).open(null, null, fromDraft);
    expect(readDraft(me, "old")).toBeNull();
    expect(readDraft(me, "recent")).not.toBeNull();
    expect(localStorage.getItem("nocturne.custom-report-draft.r9")).toBeNull();
  });

  it("keeps no draft of a deleted report, and none of any member once a session ends", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    writeDraft(me, "r1", stateText(report), saved_at);
    writeDraft(me, "r2", stateText(report), saved_at);
    writeDraft({ tenant: "dev", subject: "subject-2" }, "r2", stateText(report), saved_at);
    localStorage.setItem("nocturne.app.preference", "kept");

    // What the viewer and the list do after a delete.
    clearDraft(me, "r1");
    expect(readDraft(me, "r1")).toBeNull();
    expect(readDraft(me, "r2")).not.toBeNull();

    clearMemberStorage();
    expect(localStorage.length).toBe(1);
    expect(localStorage.getItem("nocturne.app.preference")).toBe("kept");
  });
});

describe("undo of a removal", () => {
  it("puts a removed block back where it was and opens it", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    report.add("figures");
    report.add("table");
    const [first, second] = report.blocks;
    report.remove(second.key);
    expect(report.blocks.map((b) => b.key)).not.toContain(second.key);
    expect(report.restoreBlock(second, 1)).toBeNull();
    expect(report.blocks.map((b) => b.key)).toEqual([first.key, second.key, report.blocks[2].key]);
    expect(report.open).toBe(second.key);
    // A second undo of the same removal changes nothing.
    expect(report.restoreBlock(second, 1)).toBeNull();
    expect(report.blocks).toHaveLength(3);
  });

  it("puts a removed signal back as it was, from the toast's Undo", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const ws = (report.blocks[0] as ChartBlock).ws;
    const before = JSON.stringify(ws.state);
    const id = ws.shown[0];
    expect(removeWithUndo(ws, "Removed.", () => ws.toggleSignal(id), () => ws.placeSignal(id))).toBe(true);
    expect(ws.shown).not.toContain(id);
    expect(toasts.shown).toHaveLength(1);
    toasts.shown[0].undo();
    expect(JSON.stringify(ws.state)).toBe(before);
    expect(toasts.errors).toEqual([]);
  });

  it("adds the signal again, keeping later changes, when the chart changed after the removal", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const ws = (report.blocks[0] as ChartBlock).ws;
    const id = ws.shown[0];
    const readd = vi.fn(() => ws.placeSignal(id));
    removeWithUndo(ws, "Removed.", () => ws.toggleSignal(id), readd);
    ws.title = "Changed after";
    toasts.shown[0].undo();
    expect(readd).toHaveBeenCalledOnce();
    expect(ws.shown).toContain(id);
    expect(ws.title).toBe("Changed after");
  });

  it("keeps the signal when it was added back from the catalogue before Undo", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const ws = (report.blocks[0] as ChartBlock).ws;
    const id = ws.shown[0];
    removeWithUndo(ws, "Removed.", () => ws.toggleSignal(id), () => ws.placeSignal(id));
    // What the catalogue does when the member adds the signal again.
    expect(ws.toggleSignal(id)).toBeNull();
    expect(ws.shown).toContain(id);
    toasts.shown[0].undo();
    expect(ws.shown).toContain(id);
    expect(toasts.errors).toEqual([]);
  });

  it("closes every undo toast when the workspace goes away, so none acts on a report no longer shown", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const ws = (report.blocks[0] as ChartBlock).ws;
    removeWithUndo(ws, "Removed.", () => ws.toggleSignal(ws.shown[0]), () => null);
    offerUndo("Removed a block.", () => null);
    dismissUndos();
    expect(toasts.dismissed).toEqual([1, 2]);
    dismissUndos();
    expect(toasts.dismissed).toEqual([1, 2]);
  });

  it("puts back a chart changed signal by signal, and refuses once its blocks changed", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    report.add("figures");
    const chart = report.blocks[0] as ChartBlock;
    const before = JSON.stringify(report.toState());
    const undo = report.changeSignalBySignal(chart);
    if (typeof undo === "string") throw new Error(undo);
    expect(report.blocks.some((b) => b.key === chart.key)).toBe(false);
    expect(undo()).toBeNull();
    expect(JSON.stringify(report.toState())).toBe(before);
    expect(report.open).toBe(chart.key);

    const again = report.changeSignalBySignal(chart);
    if (typeof again === "string") throw new Error(again);
    const given = report.blocks.find((b) => b.kind === "given")!;
    report.setBlockTitle(given, "Renamed after");
    expect(again()).toMatch(/Could not undo/);
    expect(report.blocks.some((b) => b.key === chart.key)).toBe(false);
  });

  it("offers no undo when the removal is refused", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const ws = (report.blocks[0] as ChartBlock).ws;
    expect(removeWithUndo(ws, "Removed.", () => "Refused.", () => null)).toBe(false);
    expect(toasts.shown).toEqual([]);
  });

  it("says why an undo cannot be done", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const ws = (report.blocks[0] as ChartBlock).ws;
    const id = ws.shown[0];
    removeWithUndo(ws, "Removed.", () => ws.toggleSignal(id), () => "Could not put it back.");
    ws.title = "Changed after";
    toasts.shown[0].undo();
    expect(toasts.errors).toEqual(["Could not put it back."]);
  });
});

describe("a custom range on the saved view", () => {
  const spec = {
    version: 1,
    range: { hours: 168 },
    panels: [
      { id: "a", kind: "chart", range: { hours: 168 }, dataset: { name: "glucose_series" } },
      { id: "b", kind: "chart", range: { hours: 24 }, dataset: { name: "glucose_series" } },
    ],
  };
  const zone = "Pacific/Auckland";
  const now = Date.parse("2026-11-16T00:00:00Z");
  const browserZone = process.env.TZ;

  beforeEach(() => {
    // A browser in another zone than the patient's: the dates are still the patient's days.
    process.env.TZ = "America/New_York";
  });

  afterEach(() => {
    process.env.TZ = browserZone;
  });

  it("runs the report and each block that follows it over the patient's days, in the patient's zone", () => {
    const span = { from: "2026-11-01", to: "2026-11-10" };
    const range = viewerRange(liveCatalogue, spec, CUSTOM_RANGE, span, zone, now);
    // New Zealand daylight time is UTC+13: midnight on 1 November there is 11:00 UTC on 31 October.
    const instants = { from: "2026-10-31T11:00:00.000Z", to: "2026-11-10T11:00:00.000Z" };
    expect(spanRange(span, zone)).toEqual(instants);
    expect(range.value).toBe(CUSTOM_RANGE);
    expect(range.spanReason).toBeNull();
    expect(range.spec.range).toEqual(instants);
    const panels = range.spec.panels as { range: unknown }[];
    expect(panels[0].range).toEqual(instants);
    expect(panels[1].range).toEqual({ hours: 24 });
    expect(range.note).toMatch(/keeps it/);
  });

  it("reads the dates as UTC days when the patient has no zone, as the other reports do", () => {
    expect(spanRange({ from: "2026-11-01", to: "2026-11-01" }, null)).toEqual({
      from: "2026-11-01T00:00:00.000Z",
      to: "2026-11-02T00:00:00.000Z",
    });
  });

  it("refuses dates longer than the report shows, and runs it as saved", () => {
    const longest = liveCatalogue.maxRangeHours / 24;
    const from = new Date(now - (longest + 5) * 86_400_000).toISOString().slice(0, 10);
    const range = viewerRange(liveCatalogue, spec, CUSTOM_RANGE, { from, to: "2026-11-16" }, zone, now);
    expect(range.value).toBe(SAVED_RANGE);
    expect(range.spanReason).toMatch(/at most/);
    expect(range.spec).toEqual(spec);
  });

  it("refuses dates that start after today", () => {
    const range = viewerRange(liveCatalogue, spec, CUSTOM_RANGE, { from: "2026-11-20", to: "2026-11-21" }, zone, now);
    expect(range.value).toBe(SAVED_RANGE);
    expect(range.spanReason).toMatch(/no data after today/);
  });
});

describe("which blocks follow the viewer's range", () => {
  const pinned = { from: "2026-11-01T00:00:00Z", to: "2026-11-08T00:00:00Z" };
  /** The workspace's state saved with the spec: a chart that follows the report, figures of their own day. */
  const builder = {
    kind: "workspace",
    version: BUILDER_VERSION,
    state: {
      blocks: [
        { kind: "chart", key: 1, chart: { range: null } },
        { kind: "figures", key: 2, range: 24, panelId: "figures" },
      ],
    },
  };
  const panelRanges = (range: ReturnType<typeof viewerRange>) => (range.spec.panels as { id: string; range: unknown }[]).map((p) => [p.id, p.range]);

  it("keeps a block's own range when the report runs over two dates", () => {
    const saved = {
      version: 1,
      range: pinned,
      panels: [
        { id: "b1-glucose", kind: "chart", range: pinned, dataset: { name: "glucose_series" } },
        { id: "figures", kind: "stat_tiles", range: { hours: 24 }, dataset: { name: "glucose_series" } },
      ],
      builder,
    };
    const range = viewerRange(liveCatalogue, saved, "7d");
    expect(panelRanges(range)).toEqual([
      ["b1-glucose", { hours: 168 }],
      ["figures", { hours: 24 }],
    ]);
    expect(range.note).toBe("A block with a range of its own keeps it. Nothing in the saved report changes.");
  });

  it("keeps a block's own range when the report has no range", () => {
    const saved = {
      version: 1,
      panels: [
        { id: "b1-glucose", kind: "chart", range: { hours: 168 }, dataset: { name: "glucose_series" } },
        { id: "figures", kind: "stat_tiles", range: { hours: 24 }, dataset: { name: "glucose_series" } },
      ],
      builder,
    };
    const range = viewerRange(liveCatalogue, saved, "14d");
    expect(panelRanges(range)).toEqual([
      ["b1-glucose", { hours: 336 }],
      ["figures", { hours: 24 }],
    ]);
    expect(range.note).toMatch(/keeps it/);
  });

  it("without the workspace's state, moves only the blocks whose range is the report's", () => {
    const saved = {
      version: 1,
      range: pinned,
      panels: [
        { id: "a", kind: "chart", range: pinned, dataset: { name: "glucose_series" } },
        { id: "b", kind: "stat_tiles", range: { hours: 24 }, dataset: { name: "glucose_series" } },
      ],
    };
    expect(panelRanges(viewerRange(liveCatalogue, saved, "7d"))).toEqual([
      ["a", { hours: 168 }],
      ["b", { hours: 24 }],
    ]);
  });
});
