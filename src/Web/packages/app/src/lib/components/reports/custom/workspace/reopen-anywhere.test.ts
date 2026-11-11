import { afterEach, describe, expect, it, vi } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import type { Report } from "./report.svelte.js";
import { BUILDER_VERSION } from "./saved-state.js";
import { STARTERS } from "./starters.js";
import { vocabularyOf } from "./workspace.svelte.js";

const vocab = vocabularyOf(liveCatalogue);

/**
 * A report saved by one member reopens in the workspace for every other only when the spec its state
 * makes does not depend on who opens it: the language of the words, or the clock times are written in.
 */

type ReportModule = { Report: typeof Report };

const twelveHour = (minute: number) => `${((Math.floor(minute / 60) + 11) % 12) + 1}:${String(minute % 60).padStart(2, "0")} ${minute < 720 ? "AM" : "PM"}`;
const twentyFourHour = (minute: number) => `${Math.floor(minute / 60)}h${String(minute % 60).padStart(2, "0")}`;

/** Every starter, then each lens and each view of the events, as one report each. */
function reports(module: ReportModule, clock: (minute: number) => string) {
  const out: { name: string; report: Report }[] = [];
  for (const starter of STARTERS) {
    out.push({ name: starter.id, report: new module.Report(liveCatalogue, starter, clock) });
    for (const lens of vocab.lenses) {
      const report = new module.Report(liveCatalogue, starter, clock);
      const chart = report.blocks[0];
      if (chart.kind !== "chart") continue;
      chart.ws.setLens(lens);
      out.push({ name: `${starter.id} as ${lens}`, report });
      if (lens !== "event") continue;
      for (const show of vocab.shows) {
        const shown = new module.Report(liveCatalogue, starter, clock);
        const block = shown.blocks[0];
        if (block.kind !== "chart") continue;
        block.ws.setLens("event");
        block.ws.setShow(show);
        out.push({ name: `${starter.id} showing ${show}`, report: shown });
      }
    }
  }
  return out;
}

/** Every `title` in a spec below its top level: a panel's, a track's or a layer's. */
function innerTitles(value: unknown, depth = 0): string[] {
  if (Array.isArray(value)) return value.flatMap((v) => innerTitles(v, depth + 1));
  if (!value || typeof value !== "object") return [];
  return Object.keys(value).flatMap((k) => {
    const v: unknown = Reflect.get(value, k);
    return k === "title" && depth > 0 && typeof v === "string" ? [v] : innerTitles(v, depth + 1);
  });
}

afterEach(() => {
  vi.doUnmock("./vocabulary-words.svelte.js");
  vi.resetModules();
});

describe("a report saved in one place", () => {
  it("writes no title the member did not type", async () => {
    const module = await import("./report.svelte.js");
    for (const { name, report } of reports(module, twelveHour))
      expect(innerTitles(report.spec()), name).toEqual([]);
  });

  it("makes the same spec under another clock format", async () => {
    const module = await import("./report.svelte.js");
    for (const { name, report } of reports(module, twelveHour)) {
      const reopened = module.Report.fromState(liveCatalogue, BUILDER_VERSION, JSON.parse(JSON.stringify(report.toState())), twentyFourHour);
      expect(reopened?.spec(), name).toEqual(report.spec());
    }
  });

  it("makes the same spec in another language", async () => {
    vi.resetModules();
    const english = reports(await import("./report.svelte.js"), twelveHour).map((r) => ({
      name: r.name,
      state: JSON.parse(JSON.stringify(r.report.toState())) as unknown,
      spec: r.report.spec(),
    }));

    // Every word the workspace shows, in another "language".
    vi.resetModules();
    vi.doMock("./vocabulary-words.svelte.js", async (load) => {
      const words: Record<string, unknown> = await load();
      const foreign = (value: unknown): unknown =>
        typeof value === "string"
          ? `«${value}»`
          : value && typeof value === "object"
            ? Object.fromEntries(Object.keys(value).map((k) => [k, foreign(Reflect.get(value, k))]))
            : value;
      return Object.fromEntries(
        Object.entries(words).map(([name, fn]) => [name, typeof fn === "function" ? (...args: unknown[]) => foreign(fn(...args)) : fn])
      );
    });
    const other = await import("./report.svelte.js");
    for (const { name, state, spec } of english) {
      const reopened = other.Report.fromState(liveCatalogue, BUILDER_VERSION, state, twentyFourHour);
      expect(reopened?.spec(), name).toEqual(spec);
    }
  });
});
