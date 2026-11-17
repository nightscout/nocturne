import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { LoopAnalysisSeries, LoopHealthUnknownReason, TimeZoneUnavailableReason } from "#lib/api/index.js";
import LoopHealthBody from "./LoopHealthBody.svelte";
import { loopHealthResponse } from "./loop-health.test-fixtures";

/** The text of the tile with this label, read afresh on every poll. */
const tile = (label: string) =>
  expect.poll(
    () => page.getByRole("term").getByText(label, { exact: true }).element().parentElement!.textContent ?? ""
  );

const STRIPS = [
  "Closed loop by hour",
  "CGM active by hour",
  "Pump active by hour",
  "Algorithm zero temps by hour", "At max basal by hour", "Sensitivity ratio by hour"];
const HIDDEN = "Hidden: your access does not include this data";

async function pickHour(strip: string, hour: number) {
  (page.getByRole("slider", { name: strip }).element() as HTMLElement).focus();
  for (let i = 0; i < hour; i++) await userEvent.keyboard("{Shift>}{ArrowRight}{/Shift}");
  await userEvent.keyboard("{Enter}");
}

describe("LoopHealthBody", () => {
  it("shows every figure as the API measured it, and the hour strips", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse() } });

    await tile("Closed loop").toContain("91.1%");
    await tile("Closed loop").not.toContain("AID active");
    await tile("CGM active").toContain("98.7%");
    await tile("Pump active").toContain("92.4%");
    await tile("Loop gaps").toMatch(/2\s*periods/);
    await tile("Loop gaps").toContain("1 h 55 min in total, longest 1 h 30 min");
    await tile("Unrecorded delivery").toContain("5 h in total");
    await tile("Algorithm zero temps").toContain("41.0%");
    await tile("Algorithm zero temps").toContain("Percent of the time with known delivery");
    await tile("Algorithm zero temps").toContain("Suspended 1.2%, manual zero temps 0.0%");
    await tile("At max basal").toContain("2.5%");
    await tile("At max basal").toMatch(/^At max basal\s*2\.5%\s*Percent of the algorithm temp basal time with a known max basal/);
    await tile("At max basal").toContain("with a known max basal (76.7%)");
    await tile("At max IOB").toContain("0.1%");
    await tile("At max IOB").toContain("at or near max IOB");
    await tile("Automatic boluses a day").toContain("54.0");
    await tile("Automatic boluses a day").toContain("Median size 0.20 U, P10–P90 0.00–0.40 U");
    await tile("Requested, not done").toContain("0.2%");
    await tile("Max IOB").toMatch(/15\.0\s*U/);

    for (const title of STRIPS) await expect.element(page.getByRole("slider", { name: title })).toBeVisible();
    await expect.element(page.getByText("1.0 = usual sensitivity")).toBeVisible();
  });

  it("lists each kind of gap longest first, each linked to Day in Review where it began", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse() } });

    await expect.element(page.getByRole("heading", { name: "Loop gaps" })).toBeVisible();
    await expect.element(page.getByRole("heading", { name: "Unrecorded delivery" })).toBeVisible();
    const links = page.getByRole("link");
    await expect.element(links.first()).toHaveAttribute("href", "/reports/day-in-review?date=2026-10-03&time=23:30");
    await expect.element(links.first()).toHaveAccessibleName(/Open in Day in review/);
    await expect.element(links.nth(1)).toHaveAttribute("href", "/reports/day-in-review?date=2026-10-05&time=03:10");
    await expect.element(links.nth(2)).toHaveAttribute("href", "/reports/day-in-review?date=2026-10-08&time=06:00");
    await expect.element(links.nth(2)).toHaveTextContent("5 h");
  });

  it("shows the longest ten of a long list, and the rest on asking", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse({ loopGapCount: 12 }) } });

    const more = page.getByRole("button", { name: "Show all 12" });
    await expect.element(more).toHaveAttribute("aria-expanded", "false");
    await expect.element(more).toHaveAttribute("aria-controls", "loop-gaps-list");
    await expect.poll(() => page.getByRole("link").elements().length).toBe(11);

    await more.click();
    await expect.poll(() => page.getByRole("link").elements().length).toBe(13);
    await expect.element(page.getByRole("button", { name: "Show the longest 10" })).toHaveAttribute("aria-expanded", "true");
  });

  it("narrows the gap lists to an hour picked on a strip, and says how many began in it", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse() } });
    await pickHour("Closed loop by hour", 3);

    await expect.element(page.getByText("1 gap started in this hour")).toBeInTheDocument();
    await expect.poll(() => page.getByRole("link").elements().length).toBe(1);
    await expect.element(page.getByRole("link")).toHaveAttribute("href", expect.stringContaining("time=03:10"));
    await expect.element(page.getByRole("heading", { name: "Unrecorded delivery" })).toBeVisible();

    await page.getByRole("button", { name: "Show all hours" }).click();
    await expect.poll(() => page.getByRole("link").elements().length).toBe(3);
  });

  it("reads gaps as unknown, never as none, when no loop cycle ran", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse({ noCycles: true }) } });

    for (const label of ["Loop gaps", "Unrecorded delivery"]) {
      await tile(label).toContain("Unknown");
      await tile(label).toContain("No loop cycles in this range");
      await tile(label).not.toMatch(/0\s*periods/);
    }
    await expect.poll(() => page.getByText("Unknown: No loop cycles in this range").elements().length).toBe(2);
    await expect.poll(() => page.getByRole("link").elements().length).toBe(0);
  });

  it("reads a cap no cycle vouched for as unknown, never as zero, with the API's reason", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse({ capUnknown: true }) } });

    await tile("At max basal").toContain("Unknown");
    await tile("At max basal").toContain("No loop cycle in this range recorded the data that this figure needs");
    await expect.element(page.getByText("Unknown: No loop cycle in this range recorded the data that this figure needs")).toBeVisible();
  });

  it("says when filters left out everything a figure is measured on", async () => {
    const response = loopHealthResponse({ capUnknown: true });
    response.primary!.unknown!.maxBasal = LoopHealthUnknownReason.MaskedOut;
    render(LoopHealthBody, { props: { response } });

    await tile("At max basal").toContain("All the data for this figure is in time that the filters exclude");
  });

  it("reads figures a system did not record as unknown, without naming the system", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse({ system: "loop" }) } });

    await tile("At max IOB").toContain("Unknown");
    await tile("Requested, not done").toContain("Unknown");
    await tile("Max IOB").toContain("Unknown");
    await tile("Max basal rate setting of the Loop app").toMatch(/3\.00\s*U\/h/);
    for (const label of ["At max IOB", "Requested, not done", "Max IOB"]) {
      await tile(label).toContain("The system that ran the loop does not record this");
      await tile(label).not.toMatch(/oref|AAPS|Trio|Loop\b/);
    }
    await expect.element(page.getByText("The loop did not report a sensitivity ratio in this range")).toBeVisible();
  });

  it("reads an oref range's unmeasured max IOB and suggestions as unknown with a neutral reason", async () => {
    render(LoopHealthBody, {
      props: { response: loopHealthResponse({ unmeasured: ["atMaxIob", "suggestedNotEnacted"] }) },
    });

    await tile("At max IOB").toContain("No loop cycle in this range recorded the data that this figure needs");
    await tile("Requested, not done").toContain("No loop cycle in this range recorded the data that this figure needs");
    await tile("Max basal rate setting of the Loop app").toContain("Only the Loop app uploads this setting");
  });

  it("says max IOB is shown only once it limited a dose", async () => {
    const response = loopHealthResponse();
    response.primary!.limits!.orefMaxIob = undefined;
    response.primary!.unknown!.orefMaxIob = LoopHealthUnknownReason.NoReasonRecorded;
    render(LoopHealthBody, { props: { response } });

    await tile("Max IOB").toContain("This figure shows only when the loop reports that max IOB limited a dose. The loop did not report this in this range.");
    await tile("Requested, not done").toContain("(open loop, or the pump did not respond)");
  });

  it("counts the loop cycles behind each hour's sensitivity ratio", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse() } });

    (page.getByRole("slider", { name: "Sensitivity ratio by hour" }).element() as HTMLElement).focus();
    await expect.element(page.getByRole("tooltip")).toHaveTextContent("14 loop cycles");
    await expect.element(page.getByRole("tooltip")).not.toHaveTextContent("readings");
  });

  it("never reads unmeasured delivery or boluses as zero", async () => {
    render(LoopHealthBody, {
      props: { response: loopHealthResponse({ unmeasured: ["zeroDelivery", "automaticBoluses"] }) },
    });

    await tile("Algorithm zero temps").toMatch(/^Algorithm zero temps\s*–/);
    await tile("Algorithm zero temps").toContain("The delivery is not known for this range");
    await tile("Algorithm zero temps").not.toContain("0.0%");
    await tile("Automatic boluses a day").toMatch(/^Automatic boluses a day\s*–/);
    await tile("Automatic boluses a day").toContain("Nothing measured in this range");
    await expect.element(page.getByText("The delivery is not known for this range").last()).toBeVisible();
  });

  it("hides what the caller's access withholds, and says so", async () => {
    render(LoopHealthBody, {
      props: { response: loopHealthResponse({ scopes: ["glucose.read", "treatments.read", "reports.read"] }) },
    });

    for (const label of ["Closed loop", "Pump active", "Loop gaps", "Unrecorded delivery", "At max basal", "At max IOB", "Max IOB", "Max basal rate setting of the Loop app"])
      await tile(label).toContain(HIDDEN);
    await tile("CGM active").toContain("98.7%");
    await tile("Algorithm zero temps").toContain("41.0%");
    await expect.element(page.getByRole("slider", { name: "Algorithm zero temps by hour" })).toBeVisible();
    await expect.element(page.getByRole("slider", { name: "CGM active by hour" })).toBeVisible();
    await expect.element(page.getByRole("slider", { name: "Pump active by hour" })).not.toBeInTheDocument();
    await expect.poll(() => page.getByRole("link").elements().length).toBe(0);
    await expect.element(page.getByText("Time when the loop ran but the pump records")).not.toBeInTheDocument();
  });

  it("hides unrecorded delivery on its own, leaving the loop gaps", async () => {
    const response = loopHealthResponse();
    response.withheldSeries = [LoopAnalysisSeries.UnrecordedDelivery];
    response.primary!.cadence!.unrecordedDelivery = undefined;
    render(LoopHealthBody, { props: { response } });

    await tile("Unrecorded delivery").toContain(HIDDEN);
    await tile("Loop gaps").toMatch(/2\s*periods/);
    await expect.poll(() => page.getByRole("link").elements().length).toBe(2);
    await expect.element(page.getByText("Time when the loop ran but the pump records")).not.toBeInTheDocument();
  });

  it("withholds max basal without therapy settings, since Loop's hours at its cap show the setting", async () => {
    render(LoopHealthBody, {
      props: { response: loopHealthResponse({ scopes: ["glucose.read", "treatments.read", "devices.read", "reports.read"] }) },
    });

    await tile("At max basal").toContain(HIDDEN);
    await tile("Loop gaps").toMatch(/2\s*periods/);
  });

  it("puts the comparison beside each figure and over each hour, with a key", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse({ comparison: true }) } });

    await tile("Closed loop").toContain("Compared: 45.5%");
    await tile("Automatic boluses a day").toContain("Compared: 27.0");
    await tile("Loop gaps").toContain("Compared: 2 periods, 1 h 55 min in total");
    await expect.poll(() => page.getByText("Compared range", { exact: true }).elements().length).toBe(6);
    await expect.poll(() => page.getByTestId("compared-hour").elements().length).toBeGreaterThan(0);

    (page.getByRole("slider", { name: "Closed loop by hour" }).element() as HTMLElement).focus();
    await expect.element(page.getByRole("tooltip")).toHaveTextContent("Compared, all days");
    await expect.element(page.getByRole("tooltip")).toHaveTextContent("Days counted");
  });

  it("draws hours resting on too few days faded, and keys them", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse({ sparse: true }) } });

    await expect.poll(() => page.getByTestId("sparse-hour").elements().length).toBeGreaterThan(0);
    await expect.poll(() => page.getByTestId("hour").elements().length).toBe(0);
    await expect.poll(() => page.getByText("Too few days", { exact: true }).elements().length).toBe(6);
  });

  it("says there is nothing to show when no time was analysed", async () => {
    render(LoopHealthBody, { props: { response: { ...loopHealthResponse(), primary: { days: [] } } } });

    await expect.element(page.getByText("No data in this range")).toBeVisible();
    await expect.element(page.getByRole("term")).not.toBeInTheDocument();
  });

  it("explains a missing time zone in place of every figure", async () => {
    render(LoopHealthBody, {
      props: {
        response: {
          ...loopHealthResponse(),
          primary: { days: [] },
          timeZoneUnavailableReason: TimeZoneUnavailableReason.NotConfigured,
        },
      },
    });

    await expect.element(page.getByText("This report needs a time zone")).toBeVisible();
    await expect.element(page.getByText("Closed loop", { exact: true })).not.toBeInTheDocument();
  });

  it("says a clamped viewer sees only the last 24 hours", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse({ historyClamped: true, days: 1 }) } });

    await expect.element(page.getByText(/This page uses only the last 24 hours/)).toBeVisible();
    await tile("Closed loop").toContain("91.1%");
  });

  it("reads each hour's share and days counted as the strip's value", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse() } });

    await expect
      .element(page.getByRole("slider", { name: "Closed loop by hour" }))
      .toHaveAttribute("aria-valuetext", expect.stringContaining("91.1%, 14 days"));
  });

  it("tells a keyboard user what Enter does on this page", async () => {
    render(LoopHealthBody, { props: { response: loopHealthResponse() } });

    const strip = page.getByRole("slider", { name: "Closed loop by hour" });
    await expect.element(strip).toHaveAccessibleDescription(/up arrow to go to the previous chart/);
    await expect.element(strip).toHaveAccessibleDescription(/Press Enter to show only the gaps that started in the hour\./);
    await expect.element(strip).not.toHaveAccessibleDescription(/list the days/);
  });
});
