import { describe, expect, it } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Report, SAME_AS_REPORT, type Block } from "./report.svelte.js";
import { STARTERS } from "./starters.js";
import { Workspace, vocabularyOf } from "./workspace.svelte.js";

const vocab = vocabularyOf(liveCatalogue);
const longest = vocab.rangePresets.at(-1)!.hours;
const maxOf = (name: string) => liveCatalogue.datasets.find((d) => d.name === name)!.maxRangeHours;

/** The block's range choice as the control shows it: its id, and whether that option is open to pick. */
function chosen(report: Report, block: Block) {
  const id = report.blockRangeId(block);
  const option = report.blockRanges(block).find((o) => o.id === id);
  return { id, reason: option ? option.reason : "missing" };
}

describe("a new block", () => {
  it.each(["figures", "table"] as const)("added as %s on the longest report range opens on a range its data covers, and says so", (kind) => {
    const report = new Report(liveCatalogue);
    expect(report.setRange(longest)).toBeNull();
    expect(report.add(kind)).toBeNull();
    const block = report.blocks.at(-1)!;
    if (block.kind !== "figures" && block.kind !== "table") throw new Error(kind);

    const max = maxOf(block.source);
    expect(report.hoursOf(block)).toBeLessThanOrEqual(max);
    expect(chosen(report, block).reason).toBeNull();
    if (max < longest) {
      expect(chosen(report, block).id).not.toBe(SAME_AS_REPORT);
      expect(report.notice).toEqual({ key: block.key, text: expect.stringMatching(/covers at most \d+ days, so this block shows the last \d+ days\.$/) });
    } else expect(report.notice).toBeNull();
  });

  it("reads another source on a range that source covers, and says so", () => {
    const report = new Report(liveCatalogue);
    report.setRange(longest);
    report.add("table");
    const table = report.blocks.at(-1)!;
    if (table.kind !== "table") throw new Error("table");
    const sources = report.tableSources.map((s) => s.dataset);
    const long = sources.find((d) => maxOf(d) >= longest);
    const short = sources.find((d) => maxOf(d) < longest);
    if (!long || !short) throw new Error("no long and short sources");
    expect(report.setSource(table, long)).toBeNull();
    expect(report.setBlockRange(table, null)).toBeNull();
    expect(report.notice).toBeNull();

    expect(report.sourceReason(table, short)).toBeNull();
    expect(report.setSource(table, short)).toBeNull();
    expect(report.hoursOf(table)).toBeLessThanOrEqual(maxOf(short));
    expect(report.notice?.key).toBe(table.key);
  });

  it("added from the catalogue as a chart takes the longest range the signal covers, and says so", () => {
    const report = new Report(liveCatalogue);
    report.setRange(longest);
    const cell = vocab.ws.cells.find((c) => c.permitted && c.maxRangeHours < longest);
    if (!cell) throw new Error("no signal shorter than the longest range");

    expect(report.addChartWith(cell.signal, cell.lens)).toBeNull();
    const chart = report.blocks.at(-1)!;
    if (chart.kind !== "chart") throw new Error("chart");
    expect(chart.ws.rangeHours).toBeLessThanOrEqual(cell.maxRangeHours);
    expect(chart.ws.incomplete).toBeNull();
    expect(report.notice).toEqual({ key: chart.key, text: expect.stringMatching(/so this chart shows the last \d+ days\.$/) });
  });

  it("leaves no notice once its range is changed", () => {
    const report = new Report(liveCatalogue);
    report.setRange(longest);
    report.add("table");
    const table = report.blocks.at(-1)!;
    if (report.notice === null) return;
    report.setBlockRange(table, vocab.rangePresets[0].hours);
    expect(report.notice).toBeNull();
  });
});

describe("a lens switch", () => {
  it("never leaves a chart that drew signals asking for one", () => {
    for (const s of STARTERS)
      for (const lens of vocab.lenses) {
        const ws = new Workspace(liveCatalogue, s);
        if (ws.incomplete !== null) continue;
        const reason = ws.lensReason(lens);
        expect(ws.setLens(lens), `${s.id} to ${lens}`).toBe(reason);
        if (reason === null) expect(ws.incomplete, `${s.id} to ${lens}`).not.toBe("Select a signal.");
        else expect(ws.lens).not.toBe(lens);
      }
  });
});
