import { afterEach, describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import type { SleepNightSummary, SleepWeekSummary } from "$lib/api";
import { regionFormat, type RegionFormat } from "$lib/stores/appearance-store.svelte";
import SleepWeeklyBreakdown from "./SleepWeeklyBreakdown.svelte";

const SUNDAY_NIGHT = "2026-01-04";
const WEDNESDAY_NIGHT = "2026-01-07";

const nights: SleepNightSummary[] = [SUNDAY_NIGHT, WEDNESDAY_NIGHT].map((displayDate) => ({
  sessionId: `session-${displayDate}`,
  displayDate,
  date: displayDate,
  sleepMinutes: 420,
}));

function weekStartingOn(weekStart: string): SleepWeekSummary {
  return {
    weekStart: `${weekStart}T00:00:00`,
    label: weekStart,
    nightCount: 2,
    daysInRange: 7,
    sessionIds: nights.map((night) => night.sessionId!),
  };
}

const savedRegion = regionFormat.current;

afterEach(() => {
  regionFormat.current = savedRegion;
});

function linkedPositions(container: HTMLElement): { positions: number[]; initials: string[] } {
  const cells = Array.from(container.querySelector("[aria-label^='Nights in']")?.children ?? []);
  return {
    positions: cells.flatMap((cell, i) => (cell.tagName === "A" ? [i] : [])),
    initials: cells.map((cell) => cell.textContent?.trim().charAt(0) ?? ""),
  };
}

describe("SleepWeeklyBreakdown", () => {
  it.each<[RegionFormat, string, number[], string[]]>([
    ["en-US", "2026-01-04", [0, 3], ["S", "M", "T", "W", "T", "F", "S"]],
    ["en-GB", "2025-12-29", [2, 6], ["M", "T", "W", "T", "F", "S", "S"]],
    ["de-DE", "2025-12-29", [2, 6], ["M", "D", "M", "D", "F", "S", "S"]],
    ["en-US", "2025-12-29", [2, 6], ["M", "T", "W", "T", "F", "S", "S"]],
    ["en-GB", "2026-01-04", [0, 3], ["S", "M", "T", "W", "T", "F", "S"]],
  ])(
    "lays out a %s caller's bucket starting %s from the bucket's own first day",
    (region, weekStart, positions, initials) => {
      regionFormat.current = region;

      const { container } = render(SleepWeeklyBreakdown, {
        weeks: [weekStartingOn(weekStart)],
        nights,
      });

      expect(linkedPositions(container)).toEqual({ positions, initials });
    }
  );
});
