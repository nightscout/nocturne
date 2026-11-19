import { describe, expect, it } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Report } from "./report.svelte.js";
import { DEFAULT_STARRED, MAX_RECENT, SignalPicks } from "./signal-picks.svelte.js";
import { searchSignals } from "./signal-search.js";
import { searchWords, sectionLabel, signalDescription, signalGroupLabel, signalLabel } from "./vocabulary-words.svelte.js";
import { vocabularyOf } from "./workspace.svelte.js";

const vocab = vocabularyOf(liveCatalogue);
const ids = vocab.signals.map((s) => s.id);
const wordsOf = (id: string) => {
  const s = vocab.signal(id)!;
  return {
    name: signalLabel(id),
    other: [signalDescription(id), sectionLabel(s.section), signalGroupLabel(s.group), ...searchWords(s.searchKeys)],
  };
};
const search = (query: string) => searchSignals(query, ids, wordsOf);

class MemoryStorage {
  data = new Map<string, string>();
  getItem(key: string) {
    return this.data.get(key) ?? null;
  }
  setItem(key: string, value: string) {
    this.data.set(key, value);
  }
}

class RefusingStorage {
  getItem(): string | null {
    throw new Error("blocked");
  }
  setItem() {
    throw new Error("blocked");
  }
}

describe("searching the signal catalogue", () => {
  it("finds a signal by its name whatever the lens, the name first", () => {
    expect(search("time in range")[0]).toBe("timeInRange");
    expect(search("TIME  in RANGE")[0]).toBe("timeInRange");
  });

  it("finds a signal by an abbreviation or another name", () => {
    expect(search("tir")).toContain("timeInRange");
    expect(search("iob")[0]).toBe("iob");
    expect(search("cob")).toContain("cob");
    expect(search("blood sugar")).toContain("glucose");
    expect(search("pod")).toEqual(expect.arrayContaining(["reservoir", "pumpBattery"]));
  });

  it("finds a signal by its section", () => {
    expect(search("loop and automation")).toEqual(expect.arrayContaining(["netLoop", "pumpModes", "tempBasals"]));
  });

  it("needs every word typed, and returns every signal in order for no text", () => {
    expect(search("heart steps")).toEqual([]);
    expect(search("  ")).toEqual(ids);
  });
});

describe("the catalogue's sections", () => {
  it("list every signal under a section and group it names", () => {
    for (const s of vocab.signals) {
      const section = vocab.sections.find((x) => x.id === s.section);
      expect(section?.groups, s.id).toContain(s.group);
    }
  });
});

describe("starred and recently used signals", () => {
  const me = { tenant: "dev", subject: "subject-1" };

  it("starts with the default stars and keeps changes in storage", () => {
    const storage = new MemoryStorage();
    const picks = new SignalPicks(me, storage as unknown as Storage);
    expect(picks.starred).toEqual([...DEFAULT_STARRED]);

    picks.toggleStar("heartRate");
    picks.toggleStar("glucose");
    for (const id of ["a", "b", "c", "d", "e", "f", "g", "b"]) picks.used(id);

    const again = new SignalPicks(me, storage as unknown as Storage);
    expect(again.isStarred("heartRate")).toBe(true);
    expect(again.isStarred("glucose")).toBe(false);
    expect(again.recent[0]).toBe("b");
    expect(again.recent).toHaveLength(MAX_RECENT);
  });

  it("keeps each member's picks apart, and none for a page with no one member", () => {
    const storage = new MemoryStorage();
    new SignalPicks(me, storage as unknown as Storage).toggleStar("heartRate");
    expect(new SignalPicks({ tenant: "dev", subject: "subject-2" }, storage as unknown as Storage).isStarred("heartRate")).toBe(false);
    expect(new SignalPicks({ tenant: "other", subject: "subject-1" }, storage as unknown as Storage).isStarred("heartRate")).toBe(false);

    const nobody = new SignalPicks({ tenant: "dev", subject: "" }, storage as unknown as Storage);
    nobody.toggleStar("steps");
    expect(nobody.isStarred("steps")).toBe(true);
    expect(new SignalPicks({ tenant: "dev", subject: "" }, storage as unknown as Storage).isStarred("steps")).toBe(false);
  });

  it("keeps working when storage refuses", () => {
    const picks = new SignalPicks(me, new RefusingStorage() as unknown as Storage);
    picks.toggleStar("steps");
    expect(picks.isStarred("steps")).toBe(true);
  });
});

describe("a signal added as a new chart", () => {
  it("opens a chart of the lens that draws it, on a range its data covers", () => {
    const report = new Report(liveCatalogue);
    report.setRange(2160);

    expect(report.addChartWith("boluses", "timeline")).toBeNull();
    const block = report.blocks.at(-1)!;
    if (block.kind !== "chart") throw new Error("chart");
    expect(report.open).toBe(block.key);
    expect(block.ws.lens).toBe("timeline");
    expect(block.ws.rangeHours).toBe(336);
    expect(block.ws.shown).toEqual(["boluses"]);
  });

  it("adds nothing when the report has no room", () => {
    const report = new Report(liveCatalogue);
    while (report.addReason("chart") === null) report.add("chart");
    const count = report.blocks.length;

    expect(report.addChartWith("glucose", "timeline")).not.toBeNull();
    expect(report.blocks).toHaveLength(count);
  });
});
