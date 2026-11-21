import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { page as pageState } from "$app/state";
import type { TenantOverviewResponse } from "#lib/api/generated/nocturne-api-client.js";
import { makePerson } from "./people-fixtures.js";

const overview = $state<{
  data: TenantOverviewResponse | undefined;
  error: unknown;
  loading: boolean;
}>({ data: undefined, error: undefined, loading: false });

const refresh = vi.hoisted(() => ({ run: async () => {} }));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getOverview: () => ({
    get current() {
      return overview.data;
    },
    get error() {
      return overview.error;
    },
    get loading() {
      return overview.loading;
    },
    refresh: () => refresh.run(),
  }),
  getReview: () => {
    throw new Error("The Now lens reads no statistics");
  },
}));

import PeoplePage from "./PeoplePage.svelte";

describe("PeoplePage", () => {
  beforeEach(() => {
    pageState.data = { baseDomain: "example.com" };
    overview.data = undefined;
    overview.error = undefined;
    overview.loading = false;
    refresh.run = async () => {};
  });

  it("shows the heading, the count and the refresh note", async () => {
    overview.data = { tenants: [makePerson({ slug: "amy" })] };
    render(PeoplePage);

    await expect
      .element(page.getByRole("heading", { name: "People", level: 1 }))
      .toBeVisible();
    await expect
      .element(page.getByText("1 person. Readings refresh every minute."))
      .toBeVisible();
  });

  it("states the empty state when the person follows nobody", async () => {
    overview.data = { tenants: [] };
    render(PeoplePage);

    await expect.element(page.getByText("You follow no other people")).toBeVisible();
    await expect
      .element(
        page.getByText(
          "When a person gives you access to their data, they show here."
        )
      )
      .toBeVisible();
  });

  it("states that the list did not load, and retries to show it", async () => {
    overview.error = new Error("boom");
    refresh.run = async () => {
      overview.error = undefined;
      overview.data = { tenants: [makePerson({ slug: "amy" })] };
    };
    render(PeoplePage);

    await expect
      .element(page.getByText("The list of people did not load."))
      .toBeVisible();
    await page.getByRole("button", { name: "Try again" }).click();

    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(
      page.getByText("The list of people did not load.").elements()
    ).toHaveLength(0);
  });

  it("shows the loading line while the first read is pending", async () => {
    overview.loading = true;
    render(PeoplePage);

    await expect.element(page.getByText("Loading…")).toBeVisible();
    expect(page.getByText("You follow no other people").elements()).toHaveLength(0);
  });
});
