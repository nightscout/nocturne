import { describe, expect, it, vi } from "vitest";
import { LOOP_ANALYSIS_PAGES } from "#lib/components/reports/loop-analysis/loop-analysis-pages.svelte.js";
import { coachCopy } from "./copy.svelte";
import { loopAnalysisTour, sequences } from "./sequences";

vi.mock("$app/paths", () => import("#lib/test-stubs/app-paths.js"));

const SHARED = "loop-analysis-pages";
const copy: Record<string, { title: string; description: string }> = coachCopy();
const loopAnalysisSteps = Object.entries(sequences)
  .filter(([name]) => name.startsWith("loop-analysis-"))
  .flatMap(([, sequence]) => sequence.steps);

describe("Loop Analysis tours", () => {
  it.each(LOOP_ANALYSIS_PAGES)("tours %s in three to five steps", (page) => {
    const tour = sequences[loopAnalysisTour(page)];

    expect(tour).toBeDefined();
    expect(tour.steps.length).toBeGreaterThanOrEqual(3);
    expect(tour.steps.length).toBeLessThanOrEqual(5);
  });

  it("explains the shared header in one step, ahead of every page tour", () => {
    expect(sequences[SHARED].steps).toHaveLength(1);
    for (const page of LOOP_ANALYSIS_PAGES)
      expect(sequences[SHARED].priority).toBeGreaterThan(sequences[loopAnalysisTour(page)].priority);
  });

  it("gives way to the setup tours and waits for no other tour", () => {
    for (const name of [SHARED, ...LOOP_ANALYSIS_PAGES.map(loopAnalysisTour)]) {
      expect(sequences[name].priority).toBeLessThan(sequences["quick-tour"].priority);
      expect(sequences[name]).not.toHaveProperty("prerequisite");
    }
  });

  it("puts each step in one tour only", () => {
    const all = Object.values(sequences).flatMap((sequence) => sequence.steps);

    expect(new Set(all).size).toBe(all.length);
  });

  it("has copy for every step", () => {
    for (const key of loopAnalysisSteps) {
      expect(copy[key]?.title, key).toBeTruthy();
      expect(copy[key]?.description, key).toBeTruthy();
    }
  });

  it("has no copy that no step shows", () => {
    const unused = Object.keys(copy).filter((key) => key.startsWith("loop-analysis-") && !loopAnalysisSteps.includes(key));

    expect(unused).toEqual([]);
  });

  it("writes every sentence in 25 words or fewer", () => {
    for (const key of loopAnalysisSteps)
      for (const sentence of copy[key].description.split(/(?<=\.)\s+/))
        expect(sentence.split(/\s+/).length, `${key}: ${sentence}`).toBeLessThanOrEqual(25);
  });

  it("tells the reader that settings evidence is observations, not settings to change", () => {
    const tour = sequences[loopAnalysisTour("settings-evidence")].steps.map((key) => copy[key].description);

    expect(tour.join(" ")).toContain("They are not settings to change.");
  });
});
