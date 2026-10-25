import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { LoopAnalysisSeries, TimeZoneUnavailableReason } from "$lib/api";
import EventResponseBody from "./EventResponseBody.svelte";
import { eventResponse, TREATMENT_SERIES } from "./event-response.test-fixtures";
import { time } from "$lib/utils/formatting";
import { atMinute } from "./daily-profile-view";

const PANELS = [
  "Glucose change from the event",
  "Glucose",
  "Net loop correction",
  "Net insulin above schedule since the event",
  "Insulin on board",
  "Carbs on board",
];

const MEALS = `Cohort 1: Meals ${time(atMinute(300))}–${time(atMinute(600))}, 30–80 g`;
const RISES = "Cohort 2: Unannounced rises";

function focusSlider(name: string) {
  const slider = page.getByRole("slider", { name, exact: true });
  (slider.element() as HTMLElement).focus();
  return slider;
}


async function iobTickValues(): Promise<number[]> {
  const panel = () => document.querySelector('[data-testid="loop-analysis-panel-iob"]');
  await expect.poll(() => panel()?.querySelectorAll("svg text").length ?? 0).toBeGreaterThan(0);
  return Array.from(panel()!.querySelectorAll("svg text"), (t) => t.textContent?.trim() ?? "")
    .filter((label) => label !== "")
    .map(Number)
    .filter(Number.isFinite);
}

// Net basal IOB goes below zero while basal is withheld, so the bands can too.
const negativeIob = { p10: -1.5, p25: -1, p50: -0.5, p75: 0, p90: 0.5, mean: -0.5, n: 14, sparse: false };

describe("EventResponseBody", () => {
  it("stacks every series on the shared axis of minutes from the event", async () => {
    render(EventResponseBody, { props: { response: eventResponse() } });

    for (const title of PANELS)
      await expect.element(page.getByRole("slider", { name: title, exact: true })).toBeVisible();
    await expect
      .element(page.getByRole("slider", { name: "Glucose", exact: true }))
      .toHaveAttribute("aria-valuemax", "71");
    await expect.element(page.getByText(/1 not included because of another event in its window/).first()).toBeVisible();
    await expect.element(page.getByText(/1 without a glucose reading at the time/).first()).toBeVisible();
  });

  it("names every cohort by its number and filters, not by colour alone", async () => {
    render(EventResponseBody, { props: { response: eventResponse() } });

    await expect.element(page.getByRole("list", { name: "Cohorts" })).toHaveTextContent(MEALS);
    await expect.element(page.getByRole("columnheader", { name: new RegExp(MEALS) })).toBeVisible();
    await expect.element(page.getByRole("columnheader", { name: new RegExp(RISES) })).toBeVisible();
    await expect.element(page.getByRole("row", { name: /in Cohort 2: Unannounced rises/ })).toBeVisible();
  });

  it("is one tab stop, and reads each cohort's median and n at the slot", async () => {
    render(EventResponseBody, { props: { response: eventResponse() } });

    const sliders = page.getByRole("slider").elements();
    expect(sliders.filter((s) => s.getAttribute("tabindex") === "0")).toHaveLength(1);

    const slider = focusSlider("Glucose change from the event");
    await userEvent.keyboard("{Shift>}{ArrowRight}{/Shift}");
    await expect.element(slider).toHaveAttribute("aria-valuenow", "12");
    await expect
      .element(slider)
      .toHaveAttribute(
        "aria-valuetext",
        `Event to +5 min: ${MEALS}: median 0, 12 events; ${RISES}: median +5, 3 events`
      );
    await userEvent.keyboard("{End}");
    await expect.element(slider).toHaveAttribute("aria-valuenow", "71");
    await userEvent.keyboard("{ArrowDown}");
    expect(document.activeElement?.getAttribute("aria-label")).toBe("Glucose");
  });

  it("lists the events behind a picked slot, each opening Day in Review", async () => {
    render(EventResponseBody, { props: { response: eventResponse() } });

    focusSlider("Glucose");
    await userEvent.keyboard("{Shift>}{ArrowRight}{/Shift}{Enter}");
    const list = page.getByRole("region", { name: "Events with a glucose reading in Event to +5 min" });
    await expect.element(list).toBeVisible();
    expect(list.getByRole("link").elements()).toHaveLength(3);

    await userEvent.keyboard("{Escape}");
    expect(page.getByRole("region", { name: /Events with a glucose reading/ }).elements()).toHaveLength(0);

    focusSlider("Glucose");
    await userEvent.keyboard("{Home}{Enter}");
    await expect.element(page.getByText("No event of this cohort has a reading here.").first()).toBeVisible();
  });

  it("compares each cohort's figures side by side, with how many came back and why grams per unit is missing", async () => {
    render(EventResponseBody, { props: { response: eventResponse({ comparison: true }) } });

    await expect.element(page.getByText("The charts show the comparison range as dashed outlines in the color of each cohort.")).toBeVisible();
    await expect.element(page.getByRole("rowheader", { name: "Observed grams per unit" })).toBeVisible();
    await expect.element(page.getByText("9.5 g/U").first()).toBeVisible();
    await expect.element(page.getByText(/Compared 9.5 g\/U/).first()).toBeVisible();
    await expect.element(page.getByText("8 of 12 events returned to within 25 mg/dL of the start glucose")).toBeVisible();
    const note = page.getByText(/grams per unit comes only from a meal/);
    await expect.element(note).toHaveTextContent(/2 meals had another event in their window,\s*3 had too little of their basal recorded,\s*1 had no insulin above schedule/);

    const links = page.getByRole("link", { name: /in Day in review/ }).elements();
    expect(links).toHaveLength(3);
    expect(links[0].getAttribute("href")).toBe("/reports/day-in-review?date=2026-10-10&time=07:30");
  });

  it("shows the fall figures for a cohort of corrections and the rise figures for a cohort of meals", async () => {
    render(EventResponseBody, { props: { response: eventResponse({ fall: true, comparison: true }) } });

    await expect.element(page.getByRole("rowheader", { name: "Drop to window end" })).toBeVisible();
    await expect.element(page.getByRole("rowheader", { name: "Time to lowest glucose" })).toBeVisible();
    await expect.element(page.getByRole("rowheader", { name: "Drop per unit above schedule" })).toBeVisible();
    await expect.element(page.getByRole("rowheader", { name: "Peak rise" })).toBeVisible();
    const below = page.getByRole("row", { name: /^Went below range/ });
    await expect.element(below).toHaveTextContent(/1 of 3 events/);
    await expect.element(below).toHaveTextContent(/Compared 1 of 3/);
    await expect.element(page.getByText(/The drop is the start glucose minus the glucose at the end of the window/)).toBeVisible();

    await expect.element(page.getByRole("columnheader", { name: "Peak rise" })).toBeVisible();
    await expect.element(page.getByRole("columnheader", { name: "Drop to window end" })).toBeVisible();
    await expect.element(page.getByRole("columnheader", { name: "Below range" })).toBeVisible();
    const rows = page.getByRole("row", { name: /in Day in review/ });
    await expect.element(rows.filter({ hasText: "Unannounced rise" })).toHaveTextContent(/\+95 mg\/dL.*–/);
    await expect.element(rows.filter({ hasText: "Correction bolus" }).first()).toHaveTextContent(/90 mg\/dL.*45 mg\/dL\/U\s*No/);
    await expect.element(rows.filter({ hasText: "Correction bolus" }).last()).toHaveTextContent(/150 mg\/dL.*75 mg\/dL\/U\s*Yes/);
  });

  it("shows no fall figure when every cohort is a rise", async () => {
    render(EventResponseBody, { props: { response: eventResponse() } });

    await expect.element(page.getByRole("rowheader", { name: "Peak rise" })).toBeVisible();
    expect(page.getByRole("rowheader", { name: "Drop to window end" }).elements()).toHaveLength(0);
    expect(page.getByRole("row", { name: /^Went below range/ }).elements()).toHaveLength(0);
    expect(page.getByRole("columnheader", { name: "Below range" }).elements()).toHaveLength(0);
    expect(page.getByRole("columnheader", { name: "Drop to window end" }).elements()).toHaveLength(0);
  });

  it("sorts the event list by a figure", async () => {
    render(EventResponseBody, { props: { response: eventResponse() } });

    await page.getByRole("button", { name: "Peak rise" }).click();
    const firstRow = () => page.getByRole("link", { name: /in Day in review/ }).elements()[0].getAttribute("href");
    expect(firstRow()).toContain("2026-10-12");
    await expect.element(page.getByRole("columnheader", { name: "Peak rise" })).toHaveAttribute("aria-sort", "descending");
    await page.getByRole("button", { name: "Peak rise" }).click();
    expect(firstRow()).toContain("2026-10-11");
  });

  it("hides the event list and every treatment series and figure the caller cannot read", async () => {
    render(EventResponseBody, { props: { response: eventResponse({ withheld: TREATMENT_SERIES }) } });

    await expect.element(page.getByRole("slider", { name: "Glucose", exact: true })).toBeVisible();
    expect(page.getByRole("slider", { name: "Insulin on board" }).elements()).toHaveLength(0);
    await expect.element(page.getByText("Hidden: your access does not include this data").first()).toBeVisible();
    expect(page.getByRole("link", { name: /in Day in review/ }).elements()).toHaveLength(0);
    expect(page.getByRole("rowheader", { name: "Observed grams per unit" }).elements()).toHaveLength(0);
    expect(page.getByText(/grams per unit comes only/).elements()).toHaveLength(0);
    await expect.element(page.getByRole("rowheader", { name: "Peak rise" })).toBeVisible();
    expect(page.getByText(/list the events with a glucose reading there/).elements()).toHaveLength(0);
  });

  it("shows the comparison when only it has events", async () => {
    const response = eventResponse({ comparison: true });
    for (const c of response.primary?.cohorts ?? []) c.events = 0;
    render(EventResponseBody, { props: { response } });

    await expect.element(page.getByRole("slider", { name: "Glucose", exact: true })).toBeVisible();
    await expect.element(page.getByText("(compared 12)")).toBeVisible();
  });

  it("explains a missing time zone in place of the curves", async () => {
    const response = eventResponse();
    response.timeZoneUnavailableReason = TimeZoneUnavailableReason.NotConfigured;
    render(EventResponseBody, { props: { response } });

    await expect.element(page.getByText("This report needs a time zone")).toBeVisible();
    expect(page.getByRole("slider").elements()).toHaveLength(0);
  });

  it("says when only the last 24 hours are included, and which choices were ignored", async () => {
    const response = eventResponse({ historyClamped: true });
    response.ignoredFilters = ["startDate", "anchor2", "isolatedOnly"];
    render(EventResponseBody, { props: { response } });

    await expect.element(page.getByText(/This page uses only the last 24 hours/)).toBeVisible();
    await expect.element(page.getByText(/Cohort 2, Isolated events only\./)).toBeVisible();
  });

  it("says so when no cohort is left to align, as without glucose", async () => {
    const response = eventResponse({ withheld: [LoopAnalysisSeries.EventGlucose] });
    response.definition = { ...response.definition, cohorts: [] };
    render(EventResponseBody, { props: { response } });

    await expect.element(page.getByText("No events to align")).toBeVisible();
    expect(page.getByRole("slider").elements()).toHaveLength(0);
  });

  it("names a window or grams value outside the limits, which the API replaced with its default", async () => {
    const response = eventResponse();
    response.ignoredFilters = ["minutesAfter", "maxGrams1", "isolatedOnly"];
    render(EventResponseBody, { props: { response } });

    await expect.element(page.getByText("The report did not use some filters")).toBeVisible();
    await expect.element(page.getByText(/outside the limits of the report, so the report used its default values:\s+After the event, Cohort 1, at most \(g\)\./)).toBeVisible();
    await expect.element(page.getByText(/so the report did not use them:\s+Isolated events only\./)).toBeVisible();
  });

  it("states the window and the options the API applied, so a printed page says what it covers", async () => {
    const response = eventResponse();
    response.definition = { ...response.definition, isolatedOnly: true };
    render(EventResponseBody, { props: { response } });

    await expect
      .element(page.getByTestId("loop-analysis-event-window"))
      .toHaveTextContent(/60 minutes before to\s+5 hours after each event\.\s+Isolated events only\./);
  });

  it("extends the IOB axis below zero to the lowest band", async () => {
    const response = eventResponse();
    for (const cohort of response.primary!.cohorts!) for (const slot of cohort.slots!) slot.iob = { ...negativeIob };
    render(EventResponseBody, { props: { response } });

    expect(Math.min(...(await iobTickValues()))).toBeLessThanOrEqual(-1.5);
  });
});
