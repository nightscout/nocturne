import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Report, type ChartBlock } from "./report.svelte.js";
import { NEW_REPORT, ReportDraft, clearDraft, readDraft, stateText, writeDraft } from "./report-draft.svelte.js";
import { BUILDER_VERSION } from "./saved-state.js";
import { starter } from "./starters.js";
import { removeWithUndo } from "./undo.svelte.js";
import { spanRange } from "./date-span.js";
import { CUSTOM_RANGE, SAVED_RANGE, viewerRange } from "./viewer-range.svelte.js";

const toasts = vi.hoisted(() => ({ shown: [] as { message: string; undo: () => void }[], errors: [] as string[] }));

vi.mock("svelte-sonner", () => ({
  toast: Object.assign(
    (message: string, options: { action: { onClick: () => void } }) => toasts.shown.push({ message, undo: options.action.onClick }),
    { error: (message: string) => toasts.errors.push(message), success: () => {} }
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
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the draft kept in this browser", () => {
  it("keeps a report's state by its id and clears it", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    writeDraft("r1", stateText(report));
    expect(readDraft("r1")).toEqual({ version: BUILDER_VERSION, state: report.toState() });
    expect(readDraft(NEW_REPORT)).toBeNull();
    clearDraft("r1");
    expect(readDraft("r1")).toBeNull();
  });

  it("keeps nothing and throws nothing when browser storage refuses", () => {
    vi.stubGlobal("localStorage", storage(true));
    const draft = new ReportDraft("r1");
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    expect(() => writeDraft("r1", stateText(report))).not.toThrow();
    expect(readDraft("r1")).toBeNull();
    expect(draft.open(report, fromDraft)).toBe(report);
    report.title = "Changed";
    expect(() => draft.keep(report)).not.toThrow();
    expect(draft.dirty).toBe(true);
  });

  it("reads a draft that is not JSON or not a state as none", () => {
    localStorage.setItem("nocturne.custom-report-draft.r1", "{not json");
    expect(readDraft("r1")).toBeNull();
    localStorage.setItem("nocturne.custom-report-draft.r1", JSON.stringify({ state: {} }));
    expect(readDraft("r1")).toBeNull();
  });

  it("writes a draft only while the report differs from how it opened, and clears it after", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const draft = new ReportDraft("r1");
    const title = report.title;
    draft.begin(report);
    draft.keep(report);
    expect(draft.dirty).toBe(false);
    expect(readDraft("r1")).toBeNull();

    report.title = "Mornings";
    draft.keep(report);
    expect(draft.dirty).toBe(true);
    expect(readDraft("r1")?.state).toEqual(report.toState());

    report.title = title;
    draft.keep(report);
    expect(draft.dirty).toBe(false);
    expect(readDraft("r1")).toBeNull();
  });

  it("opens a saved report with the changes kept for it, and with none once they are cleared", () => {
    const saved = new Report(liveCatalogue, starter("dawn-rise")!);
    const edited = Report.fromState(liveCatalogue, BUILDER_VERSION, saved.toState())!;
    edited.title = "Mornings, edited";
    writeDraft("r1", stateText(edited));

    const draft = new ReportDraft("r1");
    const shown = draft.open(saved, fromDraft);
    expect(shown?.title).toBe("Mornings, edited");
    expect(draft.restored).toBe(true);
    expect(draft.dirty).toBe(true);

    draft.clear();
    expect(readDraft("r1")).toBeNull();
    expect(new ReportDraft("r1").open(saved, fromDraft)).toBe(saved);
  });

  it("opens the report as saved, and drops the draft, when the draft no longer reads", () => {
    const saved = new Report(liveCatalogue, starter("dawn-rise")!);
    localStorage.setItem("nocturne.custom-report-draft.r1", JSON.stringify({ version: BUILDER_VERSION + 1, state: {} }));
    const draft = new ReportDraft("r1");
    expect(draft.open(saved, fromDraft)).toBe(saved);
    expect(draft.restored).toBe(false);
    expect(readDraft("r1")).toBeNull();
  });

  it("opens a new report from its draft, which counts as unsaved", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    writeDraft(NEW_REPORT, stateText(report));
    const draft = new ReportDraft(NEW_REPORT);
    expect(draft.open(null, fromDraft)?.toState()).toEqual(report.toState());
    expect(draft.dirty).toBe(true);
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
    expect(removeWithUndo(ws, "Removed.", () => ws.toggleSignal(id), () => ws.toggleSignal(id))).toBe(true);
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
    const readd = vi.fn(() => ws.toggleSignal(id));
    removeWithUndo(ws, "Removed.", () => ws.toggleSignal(id), readd);
    ws.title = "Changed after";
    toasts.shown[0].undo();
    expect(readd).toHaveBeenCalledOnce();
    expect(ws.shown).toContain(id);
    expect(ws.title).toBe("Changed after");
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
  const now = new Date(2026, 10, 16, 12).getTime();

  it("runs the report and each block that follows it from the first day's start to the last day's end", () => {
    const span = { from: "2026-11-01", to: "2026-11-10" };
    const range = viewerRange(liveCatalogue, spec, CUSTOM_RANGE, span, now);
    const instants = spanRange(span);
    expect(instants).toEqual({ from: new Date(2026, 10, 1).toISOString(), to: new Date(2026, 10, 11).toISOString() });
    expect(range.value).toBe(CUSTOM_RANGE);
    expect(range.spanReason).toBeNull();
    expect(range.spec.range).toEqual(instants);
    const panels = range.spec.panels as { range: unknown }[];
    expect(panels[0].range).toEqual(instants);
    expect(panels[1].range).toEqual({ hours: 24 });
    expect(range.note).toMatch(/keeps it/);
  });

  it("refuses dates longer than the report shows, and runs it as saved", () => {
    const longest = liveCatalogue.maxRangeHours / 24;
    const from = new Date(2026, 10, 16 - longest - 5);
    const span = { from: `${from.getFullYear()}-${String(from.getMonth() + 1).padStart(2, "0")}-${String(from.getDate()).padStart(2, "0")}`, to: "2026-11-16" };
    const range = viewerRange(liveCatalogue, spec, CUSTOM_RANGE, span, now);
    expect(range.value).toBe(SAVED_RANGE);
    expect(range.spanReason).toMatch(/at most/);
    expect(range.spec).toEqual(spec);
  });

  it("refuses dates that start after today", () => {
    const range = viewerRange(liveCatalogue, spec, CUSTOM_RANGE, { from: "2026-11-20", to: "2026-11-21" }, now);
    expect(range.value).toBe(SAVED_RANGE);
    expect(range.spanReason).toMatch(/no data after today/);
  });
});
