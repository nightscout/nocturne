import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { releaseWatercolour, resetWatercolour } from "#lib/test-stubs/watercolour-deferral.svelte.js";
import { ALL_SCOPES } from "#lib/components/reports/loop-analysis/daily-profile.test-fixtures.js";
import Harness from "./ReportsLayoutHarness.test.svelte";

vi.mock("$app/state", async () => {
  const stub = await vi.importActual<typeof import("$app/state")>("$app/state");
  const { reactiveFields } = await import("#lib/test-stubs/reactive-fields.svelte.js");
  return { ...stub, page: reactiveFields({ ...stub.page }) };
});
vi.mock("#lib/watercolour-deferral.svelte.js", () => import("#lib/test-stubs/watercolour-deferral.svelte.js"));

const PAGES = ["Daily profile", "Meals and events", "Prediction accuracy", "Loop health", "Settings evidence"];

/** The element a pointer at the centre of the given one lands on. */
function hit(element: Element) {
  const box = element.getBoundingClientRect();
  return document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
}

describe("the reports layout page switcher", () => {
  afterEach(() => resetWatercolour());

  it("takes a click on every page link once the header wash is painted", async () => {
    pageState.url = new URL("http://localhost/reports/loop-analysis/daily-profile?days=30&weekdays=6") as typeof pageState.url;
    for (const key of Object.keys(pageState.data)) delete pageState.data[key];
    Object.assign(pageState.data, { effectivePermissions: ALL_SCOPES, user: { id: "member" } });

    render(Harness, { oncontext: () => {} });
    releaseWatercolour();
    await vi.waitFor(() => expect(document.querySelector("[data-report-root] canvas")).toBeTruthy());

    const pages = page.getByRole("navigation", { name: "Loop analysis pages" });
    for (const name of PAGES) {
      const link = pages.getByRole("link", { name }).element();
      expect(link.contains(hit(link)), name).toBe(true);
      const href = new URL(link.getAttribute("href")!, "http://localhost");
      expect(href.searchParams.get("days")).toBe("30");
      expect(href.searchParams.get("weekdays")).toBe("6");
    }
  });
});
