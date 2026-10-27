import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import {
  TrackerCategory,
  type TrackerDefinitionDto,
  type TrackerInstanceDto,
} from "#lib/api/generated/nocturne-api-client.js";
import TrackerPill from "./TrackerPill.svelte";

const HOUR = 3600_000;
const now = Date.UTC(2026, 9, 25, 12, 0, 0);

const DEFINITION: TrackerDefinitionDto = {
  id: "def",
  name: "Infusion Site",
  category: TrackerCategory.Cannula,
  lifespanHours: 72,
};

/** A run started `hoursAgo` before `now`, whose read-time age is deliberately stale. */
const run = (hoursAgo: number): TrackerInstanceDto => ({
  id: "run",
  definitionId: "def",
  definitionName: "Infusion Site",
  startedAt: new Date(now - hoursAgo * HOUR).toISOString(),
  ageHours: 1,
});

const pill = () => page.getByRole("button", { name: /Infusion Site/ });
const popover = () => page.getByTestId("tracker-pill-popover");

function progress(container: HTMLElement): string | undefined {
  return (
    container.querySelector<HTMLElement>("[aria-hidden='true'][style]")?.style.getPropertyValue("--progress") ??
    undefined
  );
}

describe("TrackerPill", () => {
  it("ages on the clock it is given", async () => {
    const screen = render(TrackerPill, { instance: run(30), definition: DEFINITION, now });
    await expect.element(pill()).toHaveTextContent("1d 6h");

    await screen.rerender({ instance: run(30), definition: DEFINITION, now: now + 4 * HOUR });

    await expect.element(pill()).toHaveTextContent("1d 10h");
  });

  it("moves the time remaining and the progress bar with the clock", async () => {
    const screen = render(TrackerPill, { instance: run(36), definition: DEFINITION, now });
    expect(progress(screen.container)).toBe("0.5");

    await screen.rerender({ instance: run(36), definition: DEFINITION, now: now + 18 * HOUR });
    await pill().click();

    await expect.element(popover().getByText("18h", { exact: true })).toBeVisible();
    expect(progress(screen.container)).toBe("0.75");
  });

  it("caps the progress bar and shows overdue past the lifespan", async () => {
    const screen = render(TrackerPill, { instance: run(80), definition: DEFINITION, now });

    await pill().click();

    await expect.element(popover().getByText("Overdue")).toBeVisible();
    expect(progress(screen.container)).toBe("1");
  });

  it("counts an age of zero as an age", async () => {
    render(TrackerPill, { instance: run(0), definition: DEFINITION, now });

    await pill().click();

    await expect.element(popover().getByText("3 days")).toBeVisible();
    await expect.element(popover().getByText("Unknown")).not.toBeInTheDocument();
    await expect.element(popover().getByText("3d", { exact: true })).toBeVisible();
  });
});
