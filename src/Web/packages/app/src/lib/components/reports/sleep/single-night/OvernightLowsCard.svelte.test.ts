import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import { SleepHypoSeverity, SleepStageType } from "$lib/api";
import { time } from "$lib/utils/formatting";
import OvernightLowsCard from "./OvernightLowsCard.svelte";

const span = (start: string, end: string) => `${time(new Date(start))}–${time(new Date(end))}`;

describe("OvernightLowsCard", () => {
  it("says so when the session had no lows", async () => {
    render(OvernightLowsCard, { lows: [] });

    await expect.element(page.getByText("No low readings during this session")).toBeVisible();
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

  it("shows one time and no duration when a low has no extent", async () => {
    render(OvernightLowsCard, {
      lows: [
        {
          startAt: "2026-09-01T04:40:00Z",
          endAt: "2026-09-01T04:40:00Z",
          durationMinutes: 0,
          lowestBg: 66,
          stage: SleepStageType.Light,
          severity: SleepHypoSeverity.Low,
        },
      ],
    });

    await expect
      .element(page.getByText(time(new Date("2026-09-01T04:40:00Z")), { exact: true }))
      .toBeVisible();
    await expect.element(page.getByText(/^\d+m$/)).not.toBeInTheDocument();
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
