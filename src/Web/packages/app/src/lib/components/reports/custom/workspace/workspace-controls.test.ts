import { describe, expect, it } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { chartSource, givenSource } from "./catalogue-source.js";
import { crossesMidnight, dayPartOf, moveWindow, windowMinutes } from "./clock.js";
import { Report, type GivenBlock } from "./report.svelte.js";
import { starter } from "./starters.js";
import { Workspace } from "./workspace.svelte.js";

describe("a window of the day", () => {
  it("names the parts of the day it is exactly, with midnight as 0 or 1440", () => {
    expect(dayPartOf({ fromMinute: 1320, toMinute: 360 })).toBe("overnight");
    expect(dayPartOf({ fromMinute: 1080, toMinute: 1320 })).toBe("evening");
    expect(dayPartOf({ fromMinute: 360, toMinute: 600 })).toBeNull();
  });

  it("runs past midnight when the start is after the end", () => {
    expect(moveWindow({ fromMinute: 360, toMinute: 600 }, "from", 1320)).toEqual({ fromMinute: 1320, toMinute: 600 });
    expect(crossesMidnight({ fromMinute: 1320, toMinute: 360 })).toBe(true);
    expect(crossesMidnight({ fromMinute: 1080, toMinute: 0 })).toBe(false);
  });

  it("never makes a start and an end at the same minute, the collapse the engine refuses", () => {
    expect(moveWindow({ fromMinute: 360, toMinute: 0 }, "from", 0)).toBeNull();
    expect(moveWindow({ fromMinute: 15, toMinute: 1440 }, "from", 0)).toBeNull();
    expect(moveWindow({ fromMinute: 360, toMinute: 600 }, "to", 360)).toBeNull();
    expect(moveWindow({ fromMinute: 360, toMinute: 600 }, "to", 1440)).toEqual({ fromMinute: 360, toMinute: 0 });
  });

  it("offers every 15 minutes, and keeps a saved minute between them", () => {
    expect(windowMinutes()).toHaveLength(96);
    expect(windowMinutes([517, 1440])).toEqual(expect.arrayContaining([510, 517, 525]));
    expect(windowMinutes([517, 1440])).toHaveLength(97);
  });
});

describe("one signal against another", () => {
  it("ticks only the two signals it draws; a third kept from another view says why", () => {
    const report = new Report(liveCatalogue);
    const block = report.blocks[0];
    if (block.kind !== "chart") throw new Error("a chart first");
    for (const id of ["iob", "glucose", "cob"]) expect(block.ws.toggleSignal(id)).toBeNull();

    expect(block.ws.setLens("against")).toBeNull();
    expect(block.ws.againstPair).toEqual(["iob", "glucose"]);
    const entries = chartSource(report, block).entries;
    expect(entries.filter((e) => e.placed).map((e) => e.id)).toEqual(["glucose", "iob"]);
    expect(entries.find((e) => e.id === "cob")).toMatchObject({
      placed: false,
      reason: "This view shows two signals. Remove one first.",
    });
  });
});

describe("the catalogue of a chart held as a spec gave it", () => {
  function given(): { report: Report; block: GivenBlock } {
    const spec = new Workspace(liveCatalogue, starter("dawn-rise")!).toSpec();
    const report = Report.fromSpec(liveCatalogue, spec)!;
    const block = report.blocks[0];
    if (block.kind !== "given") throw new Error("a given chart");
    return { report, block };
  }

  it("lists only the signals along the chart's axis", () => {
    const { report, block } = given();
    const lens = report.givenLens(block);
    const source = givenSource(report, block);

    expect(source.everyView).toBe(false);
    expect(source.entries.length).toBeGreaterThan(0);
    expect(source.entries.every((e) => report.vocab.cellOf(lens, e.id) !== undefined)).toBe(true);
    expect(source.entries.length).toBeLessThan(report.vocab.signals.length);
  });

  it("adds a row to the chart for a signal picked there", () => {
    const { report, block } = given();
    const before = report.givenLayers(block).length;
    const pick = givenSource(report, block).entries.find((e) => e.drawable && e.reason === null);

    expect(pick).toBeDefined();
    expect(givenSource(report, block).toggle(pick!.id)).toBeNull();
    const after = report.blocks[0];
    expect(after.kind === "given" ? report.givenLayers(after).length : 0).toBe(before + 1);
  });
});
