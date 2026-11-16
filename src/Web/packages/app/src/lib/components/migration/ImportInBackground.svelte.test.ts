import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { MigrationJobState } from "#lib/api/index.js";
import type { MigrationJobStatus } from "#lib/api/index.js";

let status: MigrationJobStatus;
let statusError: unknown;
const statusSpy = vi.fn();

vi.mock("#lib/api/generated/migrations.generated.remote.js", () => ({
  getStatus: (jobId: string) => {
    statusSpy(jobId);
    return createQueryResource(() =>
      statusError ? Promise.reject(statusError) : Promise.resolve(status)
    );
  },
}));

import Harness from "./ImportInBackground.test-harness.svelte";
import { watchImport } from "#lib/migration/background-import.svelte.js";
import { createQueryResource } from "#lib/test-stubs/remote-resource.js";

const WATCHED_KEY = "nocturne.import.watched-job";
const notice = () => page.getByTestId("import-in-background-notice");
const banner = () => page.getByTestId("import-in-background-banner");

describe("the import that continues in the app", () => {
  beforeEach(() => {
    localStorage.clear();
    statusSpy.mockClear();
    statusError = undefined;
    status = {
      state: MigrationJobState.Running,
      progressPercentage: 40,
      recentDataReady: true,
      collectionProgress: {},
    };
  });

  it("asks the server nothing when no import is watched", async () => {
    render(Harness);

    await expect.element(notice()).not.toBeInTheDocument();
    expect(statusSpy).not.toHaveBeenCalled();
  });

  it("covers the app with the notice while a watched import runs", async () => {
    watchImport("job-1");

    render(Harness);

    await expect.element(notice()).toBeVisible();
    await expect.element(page.getByText("Your recent data is ready.")).toBeVisible();
    await expect.element(notice()).toHaveTextContent(/You can use Nocturne while the import continues/);
    await expect.element(notice()).toHaveTextContent(/40% imported/);
    await expect.element(banner()).not.toBeInTheDocument();
  });

  // A critical alarm opens over whatever is on screen. A modal notice takes pointer events from
  // the page, and the first tap on the alarm would close the notice instead of acknowledging it.
  it("leaves the rest of the page able to take a tap", async () => {
    watchImport("job-1");

    render(Harness);

    await expect.element(notice()).toBeVisible();
    expect(document.body.style.pointerEvents).not.toBe("none");
    expect(document.querySelector("[inert]")).toBeNull();
  });

  it("closes on Escape", async () => {
    watchImport("job-1");

    render(Harness);
    await expect.element(notice()).toBeVisible();

    window.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));

    await expect.element(notice()).not.toBeInTheDocument();
    await expect.element(banner()).toBeVisible();
  });

  it("follows an import named after the app loaded", async () => {
    render(Harness);
    await expect.element(banner()).not.toBeInTheDocument();

    watchImport("job-late", { acknowledged: true });

    await expect.element(banner()).toBeVisible();
  });

  it("says the newest data comes first when the recent data is not in yet", async () => {
    status = { ...status, recentDataReady: false };
    watchImport("job-1");

    render(Harness);

    await expect.element(page.getByText("Nocturne is importing your data.")).toBeVisible();
  });

  it("leaves a banner once the notice is closed, and does not cover the app again", async () => {
    watchImport("job-1");
    const first = render(Harness);

    await page.getByRole("button", { name: "Continue to Nocturne" }).click();

    await expect.element(notice()).not.toBeInTheDocument();
    await expect.element(banner()).toBeVisible();

    first.unmount();
    render(Harness);

    await expect.element(banner()).toBeVisible();
    await expect.element(notice()).not.toBeInTheDocument();
  });

  it("shows only the banner for an import started from a page that shows its progress", async () => {
    watchImport("job-1", { acknowledged: true });

    render(Harness);

    await expect.element(banner()).toBeVisible();
    await expect.element(notice()).not.toBeInTheDocument();
  });

  it("reports how the import ended and stops watching it", async () => {
    status = { ...status, state: MigrationJobState.Completed, progressPercentage: 100 };
    watchImport("job-1");
    const ended = vi.fn();

    render(Harness, { ended });

    await expect.poll(() => ended.mock.calls.map((call) => call[0])).toEqual([MigrationJobState.Completed]);
    await expect.element(notice()).not.toBeInTheDocument();
    expect(localStorage.getItem(WATCHED_KEY)).toBeNull();
  });

  it("forgets an import this session may not see", async () => {
    statusError = { status: 403 };
    watchImport("job-other-tenant");

    render(Harness);

    await expect.poll(() => localStorage.getItem(WATCHED_KEY)).toBeNull();
    await expect.element(notice()).not.toBeInTheDocument();
  });
});
