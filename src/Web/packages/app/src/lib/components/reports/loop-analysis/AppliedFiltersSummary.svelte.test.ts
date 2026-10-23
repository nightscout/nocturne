import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { DayOfWeek } from "$lib/api";
import AppliedFiltersSummary from "./AppliedFiltersSummary.svelte";

describe("AppliedFiltersSummary", () => {
  it("states the weekdays, the profile and the time left out, as the API applied them", async () => {
    render(AppliedFiltersSummary, {
      props: {
        applied: {
          days: [DayOfWeek.Monday, DayOfWeek.Tuesday],
          profileName: "Weekday",
          excludeExercise: true,
          excludeOpenLoop: true,
          excludeIllness: false,
        },
      },
    });

    const summary = page.getByTestId("loop-analysis-applied-filters");
    await expect.element(summary).toHaveTextContent(/Days: Mon, Tue\./);
    await expect.element(summary).toHaveTextContent(/Profile: Weekday\./);
    await expect.element(summary).toHaveTextContent(/Time not used: Exercise, Time not in closed loop\./);
    await expect.element(summary).not.toHaveTextContent(/Illness/);
  });

  it("says nothing when every day of every profile is used", async () => {
    render(AppliedFiltersSummary, {
      props: { applied: { days: Object.values(DayOfWeek).filter((d) => typeof d === "number") as DayOfWeek[] } },
    });

    expect(page.getByTestId("loop-analysis-applied-filters").elements()).toHaveLength(0);
  });
});
