import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";
import { normalisedSpec, statTilesResult } from "../custom-report-fixtures.js";

const calls = vi.hoisted(() => ({ runs: [] as unknown[], creates: [] as unknown[], gotos: [] as string[] }));
let response: CustomReportRunResponse;

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  runCustomReport: (arg: unknown) => {
    calls.runs.push(arg);
    return remoteQuery(() => response);
  },
  createCustomReport: remoteCommand((arg) => {
    calls.creates.push(arg);
    return { id: "new-id", name: "Saved" };
  }),
}));

vi.mock("$app/navigation", () => ({
  goto: (url: string) => {
    calls.gotos.push(url);
    return Promise.resolve();
  },
}));

import NewCustomReportPage from "./+page.svelte";

function openWith(search: string) {
  pageState.url = new URL(`http://localhost/reports/custom/new${search}`) as typeof pageState.url;
}

describe("new custom report page", () => {
  beforeEach(() => {
    calls.runs.length = 0;
    calls.creates.length = 0;
    calls.gotos.length = 0;
    response = { result: statTilesResult(), spec: normalisedSpec, errors: [] };
  });

  it("sends the link's spec to the API as it is in the link, draws the report and saves it under a name", async () => {
    openWith("?spec=q1YqS8wpTVWyMtRRKkstKs7Mz1OyMjQwMDE");
    render(NewCustomReportPage, {});

    expect(calls.runs).toEqual([{ encoded: "q1YqS8wpTVWyMtRRKkstKs7Mz1OyMjQwMDE" }]);
    await expect.element(page.getByRole("heading", { name: "Two weeks" })).toBeVisible();
    await expect.element(page.getByText("Readings: 4025.")).toBeVisible();
    await expect.element(page.getByRole("link", { name: "Open the matching report" }))
      .toHaveAttribute("href", "/reports/glucose-distribution?from=2026-10-15&to=2026-10-28");

    const name = page.getByLabelText("Name");
    await expect.element(name).toHaveValue("Two weeks");
    await name.fill("Fortnight");
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(calls.creates).toEqual([{ name: "Fortnight", spec: normalisedSpec }]));
    await vi.waitFor(() => expect(calls.gotos).toEqual(["/reports/custom/new-id"]));
  });

  it("lists the API's errors by panel and field, with the spec folded, and offers no Save", async () => {
    const sent = { version: 1, panels: [{ dataset: { name: "insulin" }, primitive: "table" }] };
    response = {
      result: null,
      spec: sent,
      errors: [
        { path: "panels[0].dataset.name", message: "'insulin' is not a dataset. Send one of: glucose_series." },
        { path: "panels[0].primitive", message: "Missing." },
      ],
    };
    openWith("?spec=abc");
    render(NewCustomReportPage, {});

    const list = page.getByTestId("spec-errors");
    await expect.element(list.getByText("panels[0].dataset.name")).toBeVisible();
    await expect.element(list.getByText("'insulin' is not a dataset. Send one of: glucose_series.")).toBeVisible();
    await expect.element(list.getByText("panels[0].primitive")).toBeVisible();
    await expect.element(page.getByText("Show the spec")).toBeVisible();
    await expect.element(page.getByText('"insulin"', { exact: false })).not.toBeVisible();
    await page.getByText("Show the spec").click();
    await expect.element(page.getByText('"insulin"', { exact: false })).toBeVisible();
    expect(page.getByRole("button", { name: "Save" }).elements()).toHaveLength(0);
  });

  it("refuses a link with no spec, or one too long, without calling the API", async () => {
    openWith("");
    const first = render(NewCustomReportPage, {});
    await expect.element(page.getByText("This link has no report in it.", { exact: false })).toBeVisible();
    await first.unmount();

    openWith(`?spec=${"A".repeat(6 * 1024 + 1)}`);
    render(NewCustomReportPage, {});
    await expect.element(page.getByText("This link is too long.", { exact: false })).toBeVisible();
    expect(calls.runs).toEqual([]);
  });

  it("writes a title with markup in it as text", async () => {
    response = { result: statTilesResult('<img src="x" onerror="alert(1)">'), spec: normalisedSpec, errors: [] };
    openWith("?spec=abc");
    render(NewCustomReportPage, {});

    await expect.element(page.getByRole("heading", { name: '<img src="x" onerror="alert(1)">' })).toBeVisible();
    expect(document.querySelector('img[src="x"]')).toBeNull();
  });
});
