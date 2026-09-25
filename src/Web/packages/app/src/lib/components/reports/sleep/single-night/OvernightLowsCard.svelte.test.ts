import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import { SleepHypoSeverity, SleepStageType } from "$lib/api";
import OvernightLowsCard from "./OvernightLowsCard.svelte";

describe("OvernightLowsCard", () => {
  it("says so when the session had no lows", async () => {
    render(OvernightLowsCard, { lows: [] });

    await expect.element(page.getByText("No low readings during this session")).toBeVisible();
  });

  it("names each low's severity, duration, stage and lowest reading", async () => {
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

    await expect.element(page.getByText("Very low")).toBeVisible();
    await expect.element(page.getByText("During Deep sleep")).toBeVisible();
    await expect.element(page.getByText("48")).toBeVisible();
    await expect.element(page.getByText(/^Lowest, /)).toBeVisible();
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
