import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { TimeZoneUnavailableReason } from "$lib/api";
import DailyProfileBody from "./DailyProfileBody.svelte";
import { profileResponse } from "./daily-profile.test-fixtures";

const focused = () => document.activeElement?.getAttribute("aria-label");

async function hoverSlot(panel: string, steps: number) {
  const slider = page.getByRole("slider", { name: panel });
  (slider.element() as HTMLElement).focus();
  for (let i = 0; i < steps; i++) await userEvent.keyboard("{Shift>}{ArrowRight}{/Shift}");
  return page.getByRole("tooltip");
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

describe("DailyProfileBody", () => {
  it("lays every series out on the shared time-of-day axis with the summary strip", async () => {
    render(DailyProfileBody, {
      props: { response: profileResponse(), insulinDeliveryHref: "/reports/insulin-delivery?days=14" },
    });

    for (const title of [
      "Glucose",
      "Basal",
      "Net loop correction",
      "Net loop correction by hour",
      "Insulin on board",
      "Carbs on board",
      "Manual boluses",
      "Carbs",
    ])
      await expect.element(page.getByRole("slider", { name: title, exact: true })).toBeVisible();

    await expect.element(page.getByText("Days with data")).toBeVisible();
    await expect.element(page.getByText("30.0", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Median 30.0, over 14 days")).toBeVisible();
    await expect.element(page.getByText("47% / 53%")).toBeVisible();
    await expect.element(page.getByText("10.0 / 6.0", { exact: true })).toBeVisible();
    await expect.element(page.getByText("33% / 20% of total insulin")).toBeVisible();
    await expect.element(page.getByText("0.88×")).toBeVisible();
    await expect.element(page.getByText("+9.60")).toBeVisible();
    await expect.element(page.getByText("32% of total insulin, over 14 days")).toBeVisible();
    await expect
      .element(page.getByRole("link", { name: /Insulin Delivery shows the totals for the full range/ }))
      .toHaveAttribute("href", "/reports/insulin-delivery?days=14");
  });

  it("describes each panel's slider by its unit and the keys it takes, with no region per panel", async () => {
    render(DailyProfileBody, { props: { response: profileResponse() } });

    const slider = page.getByRole("slider", { name: "Glucose", exact: true });
    await expect.element(slider).toBeVisible();
    const [unit, keys] = slider.element().getAttribute("aria-describedby")!.split(" ").map((id) => document.getElementById(id));
    expect(unit?.textContent).toBeTruthy();
    expect(keys?.textContent).toMatch(/Use the left and right arrows/);
    expect(page.getByRole("region", { name: "Glucose", exact: true }).elements()).toHaveLength(0);
  });

  it("is one tab stop, and the arrows move between panels keeping the slot", async () => {
    render(DailyProfileBody, { props: { response: profileResponse() } });

    const sliders = page.getByRole("slider").elements();
    expect(sliders.filter((s) => s.getAttribute("tabindex") === "0")).toHaveLength(1);
    expect(sliders[0].getAttribute("tabindex")).toBe("0");

    await hoverSlot("Glucose", 2);
    await userEvent.keyboard("{ArrowDown}");
    expect(focused()).toBe("Basal");
    await expect.element(page.getByRole("slider", { name: "Basal" })).toHaveAttribute("aria-valuenow", "24");
    expect(page.getByRole("slider").elements().filter((s) => s.getAttribute("tabindex") === "0")).toHaveLength(1);
    await expect.element(page.getByRole("slider", { name: "Basal" })).toHaveAttribute("tabindex", "0");

    await userEvent.keyboard("{ArrowUp}");
    expect(focused()).toBe("Glucose");
  });

  it("reads the slot's headline value and n as the slider's value", async () => {
    render(DailyProfileBody, { props: { response: profileResponse() } });

    await hoverSlot("Insulin on board", 1);
    await expect
      .element(page.getByRole("slider", { name: "Insulin on board" }))
      .toHaveAttribute("aria-valuetext", expect.stringContaining("Median 1.50 U, 14 days"));
  });

  it("goes to either end of the day with Home and End", async () => {
    render(DailyProfileBody, { props: { response: profileResponse() } });
    const slider = page.getByRole("slider", { name: "Glucose" });

    await hoverSlot("Glucose", 1);
    await userEvent.keyboard("{End}");
    await expect.element(slider).toHaveAttribute("aria-valuenow", "287");
    await userEvent.keyboard("{Home}");
    await expect.element(slider).toHaveAttribute("aria-valuenow", "0");
  });

  it("opens the day list on a click without moving focus, so the page does not scroll", async () => {
    render(DailyProfileBody, { props: { response: profileResponse() } });

    await page.getByRole("slider", { name: "Glucose" }).click();
    const heading = page.getByRole("heading", { name: /^Days for/ });
    await expect.element(heading).toBeVisible();
    await expect.element(heading).not.toHaveFocus();
  });

  it("moves focus to the day list it opens, and back to the panel when Escape closes it", async () => {
    render(DailyProfileBody, { props: { response: profileResponse() } });

    await hoverSlot("Carbs on board", 3);
    await userEvent.keyboard("{Enter}");
    const heading = page.getByRole("heading", { name: /^Days for/ });
    await expect.element(heading).toHaveFocus();

    await userEvent.keyboard("{Escape}");
    expect(heading.elements()).toHaveLength(0);
    expect(focused()).toBe("Carbs on board");
  });

  it("shows the slot's percentiles and n in the tooltip", async () => {
    render(DailyProfileBody, { props: { response: profileResponse() } });

    const tooltip = await hoverSlot("Insulin on board", 1);
    await expect.element(tooltip).toBeVisible();
    await expect.element(tooltip.getByText("P90")).toBeVisible();
    await expect.element(tooltip.getByText("2.50")).toBeVisible();
    await expect.element(tooltip.getByText("14 days")).toBeVisible();
    await expect.element(tooltip.getByText("Mostly as the loop reported it")).toBeVisible();
    expect(tooltip.getByText("Too few days to show a pattern").elements()).toHaveLength(0);
  });

  it("marks sparse slots as too thin to read as a pattern", async () => {
    render(DailyProfileBody, { props: { response: profileResponse({ sparse: true }) } });

    const tooltip = await hoverSlot("Glucose", 1);
    await expect.element(tooltip.getByText("Too few days to show a pattern")).toBeVisible();
    await expect.element(page.getByText(/Faded areas have too few days/)).toBeVisible();
  });

  it("draws the comparison and reads it beside the primary range", async () => {
    render(DailyProfileBody, { props: { response: profileResponse({ comparison: true }) } });

    await expect
      .element(page.getByText("The charts show the comparison range as dashed outlines on each series."))
      .toBeVisible();
    const tooltip = await hoverSlot("Insulin on board", 2);
    await expect.element(tooltip.getByText("Compared")).toBeVisible();
    await expect.element(tooltip.getByRole("row", { name: "Median 1.50 0.75" })).toBeVisible();
  });

  it("lists the days behind a picked slot, each linking to Day in Review at that time", async () => {
    render(DailyProfileBody, { props: { response: profileResponse({ comparison: true }) } });

    await hoverSlot("Glucose", 8);
    await userEvent.keyboard("{Enter}");

    await expect.element(page.getByRole("heading", { name: /^Days for/ })).toBeVisible();
    await expect.element(page.getByRole("heading", { name: "Comparison range" })).toBeVisible();
    const link = page.getByRole("link").first();
    await expect.element(link).toHaveAttribute("href", "/reports/day-in-review?date=2026-10-01&time=08:00");
  });

  it("marks the day that has not ended in each range of the day list", async () => {
    const response = profileResponse({ comparison: true });
    response.primary!.days!.at(-1)!.complete = false;
    response.comparison!.days!.at(-1)!.complete = false;
    render(DailyProfileBody, { props: { response } });

    await hoverSlot("Glucose", 8);
    await userEvent.keyboard("{Enter}");

    const links = page.getByRole("link");
    await expect.element(links.nth(13)).toHaveTextContent("Part of the day");
    await expect.element(links.nth(27)).toHaveTextContent("Part of the day");
    expect(page.getByText("Part of the day").elements()).toHaveLength(2);
  });

  it("hides every series the response withholds from a caller with glucose access only", async () => {
    render(DailyProfileBody, {
      props: { response: profileResponse({ scopes: ["glucose.read", "reports.read"] }) },
    });

    const sliders = page.getByRole("slider").elements().map((s) => s.getAttribute("aria-label"));
    expect(sliders).toEqual(["Glucose"]);
    for (const title of [
      "Basal",
      "Net loop correction",
      "Net loop correction by hour",
      "Insulin on board",
      "Carbs on board",
      "Manual boluses",
      "Carbs",
    ]) {
      const heading = page.getByRole("heading", { name: title, exact: true });
      await expect.element(heading).toBeVisible();
      const panel = heading.element().parentElement!.parentElement!;
      expect(panel.textContent).toContain("Hidden: your access does not include this data");
    }
    for (const figure of ["Total insulin", "Delivered basal ratio", "Net loop correction"])
      await expect.element(page.getByText(figure, { exact: true }).first()).toBeVisible();
    await expect.element(page.getByText("Target range hidden: your access does not include it")).toBeVisible();
    expect(page.getByText("Active target range").elements()).toHaveLength(0);
    await expect.element(page.getByRole("switch", { name: "Manual temp" })).toBeDisabled();
  });

  it("keeps treatment series but hides the schedule without therapy access", async () => {
    render(DailyProfileBody, {
      props: { response: profileResponse({ scopes: ["glucose.read", "treatments.read", "reports.read"] }) },
    });

    await expect.element(page.getByRole("slider", { name: "Insulin on board" })).toBeVisible();
    await expect
      .element(page.getByText("Scheduled basal hidden: your access does not include it"))
      .toBeVisible();
    expect(page.getByText("Scheduled", { exact: true }).elements()).toHaveLength(0);
    await expect.element(page.getByText("Total insulin", { exact: true })).toBeVisible();
    await expect.element(page.getByText("30.0", { exact: true })).toBeVisible();
  });

  it("explains a missing time zone instead of drawing anything", async () => {
    render(DailyProfileBody, {
      props: {
        response: {
          primary: { slots: [], days: [] },
          timeZoneUnavailableReason: TimeZoneUnavailableReason.NotConfigured,
          historyClamped: false,
          ignoredFilters: [],
        },
      },
    });

    await expect.element(page.getByText("This report needs a time zone")).toBeVisible();
    await expect.element(page.getByText(/The profile settings do not have a time zone/)).toBeVisible();
    expect(page.getByRole("slider").elements()).toHaveLength(0);
  });

  it("says why a shared link has no time-of-day view", async () => {
    render(DailyProfileBody, {
      props: {
        response: {
          primary: { slots: [], days: [] },
          timeZoneUnavailableReason: TimeZoneUnavailableReason.Share,
        },
      },
    });

    await expect.element(page.getByText(/A shared link does not show the time zone/)).toBeVisible();
  });

  it("says the ranges were clamped to the last 24 hours", async () => {
    render(DailyProfileBody, { props: { response: profileResponse({ historyClamped: true }) } });

    await expect.element(page.getByText(/This page uses only the last 24 hours/)).toBeVisible();
  });

  it("names the filters the caller's access made the API ignore", async () => {
    render(DailyProfileBody, {
      props: { response: { ...profileResponse(), ignoredFilters: ["profileName", "excludeOpenLoop"] } },
    });

    await expect.element(page.getByText("The report did not use some filters")).toBeVisible();
    await expect.element(page.getByText(/Profile, Time not in closed loop\./)).toBeVisible();
  });

  it("tells a keyboard user what Enter and Escape do on this page", async () => {
    render(DailyProfileBody, { props: { response: profileResponse() } });

    await expect
      .element(page.getByRole("slider", { name: "Glucose" }))
      .toHaveAccessibleDescription(/Press Enter to list the days for the time\. Press Escape to close the list\./);
  });

  it("extends the IOB axis below zero to the lowest band", async () => {
    const response = profileResponse();
    for (const slot of response.primary!.slots!) slot.iob = { ...negativeIob };
    render(DailyProfileBody, { props: { response } });

    expect(Math.min(...(await iobTickValues()))).toBeLessThanOrEqual(-1.5);
  });

  it("starts the IOB axis at zero when no band goes below it", async () => {
    render(DailyProfileBody, { props: { response: profileResponse() } });

    expect(Math.min(...(await iobTickValues()))).toBe(0);
  });
});
