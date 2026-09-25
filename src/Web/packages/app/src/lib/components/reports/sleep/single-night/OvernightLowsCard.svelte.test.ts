import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import { SleepHypoSeverity, SleepStageType } from "$lib/api";
import { time } from "$lib/utils/formatting";
import OvernightLowsCard from "./OvernightLowsCard.svelte";

const span = (start: string, end: string) => `${time(new Date(start))}–${time(new Date(end))}`;

describe("OvernightLowsCard", () => {
  it("says a night with only brief dips had no lows of fifteen minutes, not no low readings", async () => {
    // A dip shorter than fifteen minutes reaches the chart as time below range but is not a low
    // event, so the report sends an empty list.
    render(OvernightLowsCard, { lows: [] });

    await expect
      .element(page.getByText("No lows lasting 15 minutes or more during this session", { exact: true }))
      .toBeVisible();
    await expect.element(page.getByText(/No low readings/)).not.toBeInTheDocument();
    await expect.element(page.getByText(/below range for 15 minutes or more/)).toBeVisible();
  });

  it("explains what counts as a low alongside the list", async () => {
    render(OvernightLowsCard, {
      lows: [
        {
          startAt: "2026-09-01T02:10:00Z",
          endAt: "2026-09-01T02:25:00Z",
          durationMinutes: 15,
          lowestBg: 62,
          stage: SleepStageType.Light,
          severity: SleepHypoSeverity.Low,
        },
      ],
    });

    await expect.element(page.getByText(/below range for 15 minutes or more/)).toBeVisible();
  });

  it("names each low's time span, severity, duration, stage and lowest reading", async () => {
    render(OvernightLowsCard, {
      lows: [
        {
          startAt: "2026-09-01T02:10:00Z",
          endAt: "2026-09-01T02:35:00Z",
          durationMinutes: 25,
          lowestBg: 48,
          stage: SleepStageType.Deep,
          severity: SleepHypoSeverity.VeryLow,
        },
      ],
    });

    await expect
      .element(page.getByText(span("2026-09-01T02:10:00Z", "2026-09-01T02:35:00Z")))
      .toBeVisible();
    await expect.element(page.getByText("Very low")).toBeVisible();
    await expect.element(page.getByText("25m", { exact: true })).toBeVisible();
    await expect.element(page.getByText("During deep sleep", { exact: true })).toBeVisible();
    await expect.element(page.getByText("48")).toBeVisible();
    await expect.element(page.getByText(/^Lowest, /)).toBeVisible();
  });

  it("keeps REM in capitals in the stage phrase", async () => {
    render(OvernightLowsCard, {
      lows: [
        {
          startAt: "2026-09-01T02:10:00Z",
          endAt: "2026-09-01T02:25:00Z",
          durationMinutes: 15,
          lowestBg: 62,
          stage: SleepStageType.Rem,
          severity: SleepHypoSeverity.Low,
        },
      ],
    });

    await expect.element(page.getByText("During REM sleep", { exact: true })).toBeVisible();
  });

  it("shows a low read from a single fifteen-minute reading with its span and duration", async () => {
    render(OvernightLowsCard, {
      lows: [
        {
          startAt: "2026-09-01T03:00:00Z",
          endAt: "2026-09-01T03:15:00Z",
          durationMinutes: 15,
          lowestBg: 66,
          stage: SleepStageType.Light,
          severity: SleepHypoSeverity.Low,
        },
      ],
    });

    await expect
      .element(page.getByText(span("2026-09-01T03:00:00Z", "2026-09-01T03:15:00Z")))
      .toBeVisible();
    await expect.element(page.getByText("15m", { exact: true })).toBeVisible();
    await expect.element(page.getByText("66")).toBeVisible();
  });

  it("omits the stage when no stage covered the lowest reading", async () => {
    render(OvernightLowsCard, {
      lows: [
        {
          startAt: "2026-09-01T02:10:00Z",
          endAt: "2026-09-01T02:25:00Z",
          durationMinutes: 15,
          lowestBg: 65,
          stage: SleepStageType.Unknown,
          severity: SleepHypoSeverity.Low,
        },
      ],
    });

    await expect.element(page.getByText("Low", { exact: true })).toBeVisible();
    await expect.element(page.getByText(/sleep$/)).not.toBeInTheDocument();
  });
});
