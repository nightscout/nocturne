import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { liveCatalogue } from "#lib/components/reports/custom/workspace/catalogue.fixtures.js";
import { BUILDER_KIND, BUILDER_VERSION } from "#lib/components/reports/custom/workspace/saved-state.js";
import { runOf, statisticsTemplate } from "../custom-report-fixtures.js";
import type { CustomReportCatalogue } from "@nocturne/api-client";

type Spec = { panels: { id: string; tracks?: { layers: { id: string }[] }[] }[]; builder?: { kind: string; version: number; state: unknown } };

const calls = vi.hoisted(() => ({ runs: [] as { spec: Spec }[], creates: [] as { name: string; spec: Spec }[], gotos: [] as string[] }));
/** The engine's refusal of the next save, or null to save it. */
let refusal: unknown = null;
/** Whether a preview run fails, as one does when the API cannot be reached. */
let runsFail = false;
/** The catalogue the page reads. */
let known: CustomReportCatalogue = liveCatalogue;
/** Whether each signal has data in a range, as the catalogue reads it. */
let availability: (arg: { hours: number }) => unknown = ({ hours }) => ({ hours, signals: [] });
/** Adds to a run's result what a test needs the engine to say, such as day counts. */
let decorate: (result: { panels: { tracks?: { layers: Record<string, unknown>[] }[] }[] }) => void = () => {};

vi.mock("#lib/api/report-image.remote.js", () => ({ drawReportPanelImage: () => Promise.resolve("") }));

vi.mock("#lib/api/generated/foods.generated.remote.js", () => ({
  getFoods: () => remoteQuery(() => []),
  getFood: () => remoteQuery(() => ({ id: "f", name: "Penne" })),
}));

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  getCustomReportCatalogue: () => remoteQuery(() => known),
  getCustomReportSignalAvailability: (arg: unknown) => remoteQuery(() => availability(arg as { hours: number })),
  getCustomReports: () => remoteQuery(() => []),
  getCustomReportTemplates: () => remoteQuery(() => [statisticsTemplate]),
  runCustomReport: remoteCommand((arg) => {
    const run = arg as { spec: Spec };
    calls.runs.push(run);
    if (runsFail) return Promise.reject(new Error("offline"));
    const answer = runOf(run.spec as unknown as Record<string, unknown>);
    decorate(answer.result as never);
    return answer;
  }),
  createCustomReport: remoteCommand((arg) => {
    const request = arg as { name: string; spec: Spec };
    if (refusal) return Promise.reject(refusal);
    calls.creates.push(request);
    return { id: "new-id", name: request.name };
  }),
}));

type Navigation = { type: string; to: { url: URL } | null; cancel: () => void };
const guards = vi.hoisted(() => [] as ((navigation: Navigation) => void)[]);

vi.mock("$app/navigation", () => ({
  goto: (url: string) => {
    calls.gotos.push(url);
    return Promise.resolve();
  },
  beforeNavigate: (guard: (navigation: Navigation) => void) => guards.push(guard),
}));

/** Runs the page's latest navigation guard, as SvelteKit does before a navigation; true when it cancels it. */
function navigate(type: string, to: string | null): boolean {
  let cancelled = false;
  guards.at(-1)?.({ type, to: to ? { url: new URL(to, window.location.href) } : null, cancel: () => (cancelled = true) });
  return cancelled;
}

import NewCustomReportPage from "./+page.svelte";
import { minuteLabel as minuteText } from "#lib/components/reports/custom/workspace/clock.js";

const layerIds = (spec: Spec) => spec.panels.flatMap((p) => (p.tracks ?? []).flatMap((t) => t.layers.map((l) => l.id)));

async function start(question: string) {
  render(NewCustomReportPage, {});
  await page.getByText(question, { exact: true }).click();
  await expect.element(page.getByTestId("report-workspace")).toBeVisible();
  await vi.waitFor(() => expect(calls.runs.length).toBeGreaterThan(0));
}

/** Opens the signal catalogue and finds a signal by search, leaving the catalogue open. */
async function find(name: string) {
  if (!document.querySelector("[data-testid='signal-catalogue']")) await page.getByTestId("add-signal").click();
  await page.getByTestId("catalogue-search").fill(name);
  return page.getByRole("dialog").getByRole("button", { name, exact: true });
}

async function save(name: string) {
  await page.getByTestId("save-trigger").click();
  await page.getByLabelText("Name", { exact: true }).fill(name);
  await page.getByRole("button", { name: "Save the report" }).click();
}

/** The page data of a signed-in member of a tenant, whose drafts this browser keeps. */
const MEMBER = { tenantSlug: "dev", user: { subjectId: "subject-1" } };

describe("a new custom report", () => {
  beforeEach(async () => {
    calls.runs.length = 0;
    calls.creates.length = 0;
    calls.gotos.length = 0;
    refusal = null;
    runsFail = false;
    known = liveCatalogue;
    decorate = () => {};
    availability = ({ hours }) => ({ hours, signals: [] });
    localStorage.clear();
    pageState.data = { ...MEMBER };
    await page.viewport(1280, 900);
  });

  it("starts with the questions, grouped, and a blank report", async () => {
    render(NewCustomReportPage, {});

    await expect.element(page.getByRole("heading", { name: "Start with a question" })).toBeVisible();
    await expect.element(page.getByRole("heading", { name: "Lows and highs" })).toBeVisible();
    await expect.element(page.getByText("Is there a dawn rise?", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Blank report", { exact: true })).toBeVisible();
    expect(calls.runs).toEqual([]);
  });

  it("opens a question in the workspace and previews its chart", async () => {
    await start("Is there a dawn rise?");

    expect(layerIds(calls.runs[0].spec)).toEqual(["glucoseWide", "glucose", "target"]);
    await expect.element(page.getByTestId("chart-caption")).toHaveTextContent("over a typical day");
    await expect.element(page.getByTestId("chart-preview").getByTestId("report-layered-chart")).toBeVisible();
    // The legend sits under the plot, beside what it names.
    await expect.element(page.getByTestId("chart-preview").getByTestId("report-legend")).toBeVisible();
  });

  it("runs the preview again when a signal is taken off the chart", async () => {
    await start("Is there a dawn rise?");
    const before = calls.runs.length;

    await page.getByTestId("signal-rail").getByRole("button", { name: "Remove Target range" }).click();

    await vi.waitFor(() => expect(calls.runs.length).toBeGreaterThan(before), { timeout: 3000 });
    expect(layerIds(calls.runs.at(-1)!.spec)).toEqual(["glucoseWide", "glucose"]);
  });

  it("disables a control that cannot be used now, and says why beside it", async () => {
    await start("Is there a dawn rise?");
    await page.getByRole("radio", { name: "One against another" }).click();
    await (await find("Insulin on board")).click();

    const blocked = await find("Carbs on board");
    const reason = page.getByText("This view shows two signals. Remove one first.").first();
    await expect.element(reason).toBeVisible();
    await expect.element(blocked).toBeDisabled();
    expect(blocked.element().getAttribute("aria-describedby")?.split(" ")).toContain(reason.element().id);
  });

  it("saves the spec with the workspace's state, then opens the saved report", async () => {
    await start("Is there a dawn rise?");
    await save("Dawn");

    await vi.waitFor(() => expect(calls.creates).toHaveLength(1));
    const { name, spec } = calls.creates[0];
    expect(name).toBe("Dawn");
    expect(layerIds(spec)).toEqual(["glucoseWide", "glucose", "target"]);
    expect(spec.builder).toMatchObject({ kind: BUILDER_KIND, version: BUILDER_VERSION, state: { rangeHours: 336 } });
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/new-id"]));
  });

  it("shows the engine's refusal beside Save, and stays", async () => {
    refusal = { status: 400, body: { message: "The builder state is too large. It can be at most 32 kB." } };
    await start("Is there a dawn rise?");
    await save("Dawn");

    await expect.element(page.getByRole("alert")).toHaveTextContent("The builder state is too large.");
    expect(calls.gotos).toEqual([]);
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
  });

  it("goes back to the questions with Start again", async () => {
    await start("Is there a dawn rise?");

    const restart = page.getByTestId("report-workspace").getByRole("button", { name: "Start again" });
    await restart.click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("This report is not saved.");
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect.element(page.getByTestId("report-workspace")).toBeVisible();

    await restart.click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Start again" }).click();
    await expect.element(page.getByRole("heading", { name: "Start with a question" })).toBeVisible();
  });

  it("announces each settled preview once, and only for the open block", async () => {
    await start("Is there a dawn rise?");
    const spoken = () =>
      [...document.querySelectorAll("[data-testid='report-workspace'] [aria-live]")].map((e) => e.textContent?.trim()).filter(Boolean);
    await vi.waitFor(() => expect(spoken()).toEqual([expect.stringMatching(/^Chart updated\. .*typical day/)]));

    await page.getByRole("button", { name: "Add figures" }).click();
    await vi.waitFor(() => expect(spoken()).toEqual(["Figures updated."]));
    expect(document.querySelectorAll("[role='status']").length).toBe(0);
  });

  it("clears the spinner after a failed run and shows only the failure", async () => {
    await start("Is there a dawn rise?");
    runsFail = true;
    await page.getByTestId("signal-rail").getByRole("button", { name: "Remove Target range" }).click();

    await expect.element(page.getByText("The preview could not be made.")).toBeVisible();
    await vi.waitFor(() => expect(document.querySelectorAll("[data-testid='chart-preview'] [aria-busy='true']").length).toBe(0));
    expect(document.querySelector("[data-testid='chart-preview'] .animate-spin")).toBeNull();
  });

  it("writes a group of days' count, and why days are in neither group, from the run's counts", async () => {
    decorate = (result) => {
      for (const layer of result.panels.flatMap((p) => p.tracks ?? []).flatMap((t) => t.layers))
        layer.days = layer.id === "glucose-1"
          ? { kept: 4, total: 29, matching: 4, notMatching: 24, partial: 1, undecided: 0 }
          : { kept: 24, total: 29, matching: 4, notMatching: 24, partial: 1, undecided: 2 };
    };
    render(NewCustomReportPage, {});
    await page.getByText("Days with a low against all other days", { exact: true }).click();

    const counts = page.getByTestId("day-count");
    await expect.element(counts.nth(0)).toHaveTextContent("4 of 29 days. One day is only partly in the range and is in neither group.");
    await expect.element(counts.nth(1)).toHaveTextContent("24 of 29 days. One day is only partly in the range and is in neither group. 2 days are before the history your access reads.");
  });

  it("says why a group of events is not shown, from the run's cohorts", async () => {
    decorate = (result) => {
      for (const layer of result.panels.flatMap((p) => p.tracks ?? []).flatMap((t) => t.layers))
        layer.cohorts = [
          { number: 1, label: "Meal: 06:00-08:30", omitted: { reason: "scope", scope: "food.read" } },
          { number: 2, label: "Meal: 08:30-11:00", omitted: { reason: "sibling", group: 1 } },
        ];
    };
    await start("What does a later breakfast do?");

    const rows = page.getByTestId("moment-row");
    await expect.element(rows.nth(0).getByTestId("left-out")).toHaveTextContent("Not shown: your access does not include food.");
    await expect.element(rows.nth(1).getByTestId("left-out")).toHaveTextContent("Not shown: group A is not shown.");
  });

  it("puts a signal the reader's access cannot read under Not in your access, with the scope it needs", async () => {
    const limited: CustomReportCatalogue = structuredClone(liveCatalogue);
    for (const cell of limited.workspace.cells)
      if (cell.signal === "heartRate") Object.assign(cell, { permitted: false, scopes: ["heartrate.read"] });
    for (const signal of limited.workspace.signals) if (signal.id === "heartRate") signal.permitted = false;
    known = limited;
    await start("The last 24 hours");

    await page.getByTestId("add-signal").click();
    const group = page.getByTestId("catalogue-no-access");
    await expect.element(group).toHaveTextContent("Not in your access (1)");
    await group.click();
    const item = page.getByRole("dialog").getByRole("button", { name: "Heart rate", exact: true });
    await expect.element(item).toBeDisabled();
    const why = (item.element().getAttribute("aria-describedby") ?? "").split(" ").map((id) => document.getElementById(id)?.textContent ?? "").join(" ");
    expect(why).toContain("Your access cannot read heart rate, which this needs.");
  });

  it("finds a signal another view draws, and switches to that view", async () => {
    await start("The last 24 hours");

    await find("time in range");
    await expect.element(page.getByText(/^\d+ signals found\.$/)).toBeInTheDocument();
    await expect.element(page.getByRole("dialog").getByRole("button", { name: "Time in range", exact: true })).toBeVisible();
    await page.getByRole("dialog").getByRole("button", { name: "Time in range", exact: true }).click();
    await expect.element(page.getByText("Timeline cannot show this signal.")).toBeVisible();
    await page.getByRole("button", { name: "Switch to Day by day" }).click();

    await expect.element(page.getByRole("dialog")).not.toBeInTheDocument();
    await expect.element(page.getByRole("radio", { name: "Day by day" })).toHaveAttribute("data-state", "on");
    await expect.element(page.getByTestId("signal-rail").getByRole("button", { name: "Remove Time in range" })).toBeVisible();
  });

  it("adds a signal another view draws as a new chart, and keeps this chart", async () => {
    await start("The last 24 hours");

    await find("time in range");
    await page.getByRole("dialog").getByRole("button", { name: "Time in range", exact: true }).click();
    await page.getByTestId("add-as-chart").click();

    await expect.element(page.getByRole("dialog")).not.toBeInTheDocument();
    await vi.waitFor(() => expect(page.getByTestId("report-blocks").getByRole("listitem").elements().filter((e) => e.matches("li[data-block-kind]"))).toHaveLength(2));
    await expect.element(page.getByTestId("signal-rail").getByRole("button", { name: "Remove Time in range" })).toBeVisible();
    await vi.waitFor(() => expect(document.activeElement?.getAttribute("data-testid")).toBe("add-signal"));
  });

  it("stars a signal, and lists it under Starred the next time", async () => {
    await start("The last 24 hours");

    await page.getByTestId("add-signal").click();
    await page.getByRole("button", { name: "Activity and body" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Star Heart rate" }).first().click();
    await userEvent.keyboard("{Escape}");
    await expect.element(page.getByTestId("add-signal")).toHaveFocus();

    await page.getByTestId("add-signal").click();
    const starred = page.getByRole("region", { name: "Starred" });
    await expect.element(starred.getByRole("button", { name: "Heart rate", exact: true })).toBeVisible();
    await expect.element(starred.getByRole("button", { name: "Star Heart rate" })).toHaveAttribute("aria-pressed", "true");
  });

  it("says once how many signals need a shorter range, and applies the range that fits", async () => {
    render(NewCustomReportPage, {});
    await page.getByText("Blank report", { exact: true }).click();
    await page.getByRole("radio", { name: "90 days" }).first().click();
    await page.getByTestId("add-signal").click();

    const need = page.getByTestId("range-need").filter({ hasText: "14 days or less" });
    await expect.element(need).toHaveTextContent(/^\d+ signals need 14 days or less\. Use 14 days$/);
    expect(document.body.textContent).not.toContain("Choose a shorter range first.");
    const boluses = await find("Boluses");
    await expect.element(boluses).toBeDisabled();
    await expect.element(page.getByRole("dialog").getByText("Up to 14 days").first()).toBeVisible();

    await need.getByTestId("use-range").click();
    await expect.element(boluses).toBeEnabled();
    await boluses.click();
    await expect.element(boluses).toHaveAttribute("aria-pressed", "true");
  });

  it("puts a signal with no data in the range in one closed group at the end", async () => {
    availability = ({ hours }) => ({ hours, signals: [{ signal: "injections", state: "none" }] });
    await start("The last 24 hours");

    await page.getByTestId("add-signal").click();
    const group = page.getByTestId("catalogue-no-data");
    await expect.element(group).toHaveTextContent("No data in this range (1)");
    await group.click();
    await expect.element(page.getByRole("dialog").getByRole("button", { name: "Long-acting injections", exact: true })).toBeVisible();
  });

  it("lists the API's ready-made reports last, opens one in the workspace, and saves the spec it gave", async () => {
    render(NewCustomReportPage, {});
    const ready = page.getByTestId("ready-made-reports");
    await expect.element(ready.getByRole("heading", { name: "Ready-made reports" })).toBeVisible();

    await ready.getByText("Statistics", { exact: true }).click();

    await expect.element(page.getByTestId("report-workspace")).toBeVisible();
    await expect.element(page.getByLabelText("Report title")).toHaveValue("Two weeks");
    await expect.element(page.getByTestId("block-editor-figures")).toBeVisible();
    await save("Fortnight");
    await vi.waitFor(() => expect(calls.creates).toHaveLength(1));
    expect(calls.creates[0].spec).toMatchObject({ title: "Two weeks", range: { hours: 336 }, panels: [{ id: "p1" }] });
  });

  it("disables a lens that cannot take the chart, with the reason beside the switch", async () => {
    await start("Is there a dawn rise?");
    await page.getByRole("radio", { name: "Weekdays and the weekend" }).or(page.getByRole("button", { name: "Weekdays and the weekend" })).first().click();
    await page.getByRole("radio", { name: "Timeline" }).click();
    for (const signal of ["Insulin on board", "Carbs on board", "Basal delivered", "Boluses", "Carbs"])
      await (await find(signal)).click();
    await userEvent.keyboard("{Escape}");

    const typical = page.getByRole("radio", { name: "Typical day" });
    await expect.element(typical).toBeDisabled();
    const reason = page.getByTestId("lens-reason").first();
    await expect.element(reason).toHaveTextContent("Typical day:");
    expect(typical.element().getAttribute("aria-describedby")).toBe(reason.element().id);
  });

  it("offers no earlier period under a view of the events one by one, and says why once", async () => {
    await start("What does a later breakfast do?");
    await page.getByRole("radio", { name: "Spread" }).click();
    await page.getByTestId("chart-options-toggle").click();

    await expect.element(page.getByTestId("comparison-reason")).toHaveTextContent("show the events of the range only");
    await page.getByTestId("event-options").click();
    expect(page.getByRole("menuitem", { name: "Compare with an earlier period" }).elements()).toHaveLength(0);
  });

  it("disables Save while a block is unfinished, with the reason beside it", async () => {
    render(NewCustomReportPage, {});
    await page.getByText("Blank report", { exact: true }).click();

    await expect.element(page.getByTestId("save-trigger")).toBeDisabled();
    await expect.element(page.getByTestId("save-blocked")).toHaveTextContent("Add a signal or a block first.");
  });

  it("moves the focus to the chip before a removed signal", async () => {
    await start("Is there a dawn rise?");
    await page.getByTestId("signal-chips").getByRole("button", { name: /^Target range/ }).click();
    await page.getByRole("button", { name: "Remove from chart" }).click();

    await vi.waitFor(() => expect(document.activeElement?.getAttribute("data-signal-chip")).toBe("glucose"));
  });

  it("moves the focus to Narrow when a group's only narrowing is removed", async () => {
    await start("What does a later breakfast do?");
    const row = page.getByTestId("moment-row").first();
    await row.getByRole("button", { name: /^Remove time of day/ }).click();

    await vi.waitFor(() => expect(document.activeElement?.textContent?.trim()).toBe("Narrow"));
  });

  it("sets a time of day that runs past midnight, and never a start and an end at the same minute", async () => {
    await start("What does a later breakfast do?");
    await page.getByTestId("moment-row").first().getByRole("button", { name: /^Time of day:/ }).click();
    await page.getByRole("radio", { name: "Overnight" }).click();

    await expect.element(page.getByTestId("time-of-day-window")).toHaveTextContent(/past midnight/);
    await vi.waitFor(() => {
      const cohorts = JSON.stringify(calls.runs.at(-1)?.spec);
      expect(cohorts).toContain('"fromMinute":1320');
      expect(cohorts).toContain('"toMinute":360');
    });

    await page.getByLabelText("End", { exact: true }).click();
    await expect.element(page.getByRole("option", { name: minuteText(1320) })).toHaveAttribute("data-disabled");
  });

  it("removes the time of day when Full day is chosen", async () => {
    await start("What does a later breakfast do?");
    const row = page.getByTestId("moment-row").first();
    await row.getByRole("button", { name: /^Time of day:/ }).click();
    await page.getByRole("radio", { name: "Full day" }).click();

    await vi.waitFor(() => expect(row.getByRole("button", { name: /^Time of day:/ }).elements()).toHaveLength(0));
  });


  it("tells a member limited to the last 24 hours that it cannot create reports", async () => {
    pageState.data = { ...MEMBER, limitTo24Hours: true };
    render(NewCustomReportPage, {});

    await expect.element(page.getByTestId("clamped-note")).toHaveTextContent("Your access is limited to the last 24 hours. It cannot create reports.");
    expect(page.getByTestId("report-starters").elements()).toHaveLength(0);
  });

  it("offers a handed-off session a sign-in in place of the starters", async () => {
    pageState.data = { ...MEMBER, isHandedOffSession: true };
    render(NewCustomReportPage, {});

    await expect.element(page.getByText("Sign in again to make a report.")).toBeVisible();
    expect(page.getByTestId("report-starters").elements()).toHaveLength(0);
  });
});

describe("the safety net of a new report", () => {
  beforeEach(async () => {
    calls.runs.length = 0;
    calls.creates.length = 0;
    calls.gotos.length = 0;
    guards.length = 0;
    refusal = null;
    runsFail = false;
    known = liveCatalogue;
    decorate = () => {};
    availability = ({ hours }) => ({ hours, signals: [] });
    localStorage.clear();
    pageState.data = { ...MEMBER };
    await page.viewport(1280, 900);
  });

  it("restores the unsaved changes after a reload, and discards them on request", async () => {
    await start("Is there a dawn rise?");
    await page.getByLabelText("Report title").fill("Mornings, unsaved");
    await vi.waitFor(() => expect(localStorage.length).toBe(1));

    document.body.innerHTML = "";
    render(NewCustomReportPage, {});

    await expect.element(page.getByText("Restored your unsaved changes.")).toBeVisible();
    await expect.element(page.getByLabelText("Report title")).toHaveValue("Mornings, unsaved");

    await page.getByRole("button", { name: "Discard and start again" }).click();
    await expect.element(page.getByRole("heading", { name: "Start with a question" })).toBeVisible();
    expect(localStorage.length).toBe(0);
  });

  it("asks before it leaves unsaved changes, and not after a save", async () => {
    await start("Is there a dawn rise?");
    expect(navigate("link", "/reports")).toBe(false);

    await page.getByLabelText("Report title").fill("Mornings, unsaved");
    await vi.waitFor(() => expect(navigate("leave", null)).toBe(true));
    expect(navigate("link", "/reports")).toBe(true);
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Your changes are not saved.");
    await dialog.getByRole("button", { name: "Stay" }).click();
    expect(calls.gotos).toEqual([]);

    expect(navigate("link", "/reports")).toBe(true);
    await page.getByRole("alertdialog").getByRole("button", { name: "Leave" }).click();
    await vi.waitFor(() => expect(calls.gotos).toHaveLength(1));
    expect(String(calls.gotos[0])).toContain("/reports");

    await save("Dawn");
    await vi.waitFor(() => expect(calls.gotos.map(String)).toContain("/reports/custom/new-id"));
    expect(navigate("link", "/reports")).toBe(false);
    expect(localStorage.length).toBe(0);
  });
});
