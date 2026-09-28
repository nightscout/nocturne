import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import type { TenantDto } from "$lib/api/generated/nocturne-api-client";

const state = vi.hoisted(() => ({
  page: {
    url: new URL("http://sam.localhost/"),
    data: {} as Record<string, unknown>,
  },
  tenants: [] as TenantDto[],
  replaceState: vi.fn(),
}));

vi.mock("$app/state", () => ({ page: state.page }));
vi.mock("$app/navigation", () => ({ replaceState: state.replaceState }));
vi.mock("$api/generated/myTenants.generated.remote", () => ({
  getMyTenants: () => remoteQuery(() => state.tenants),
}));

import JoinWelcome from "./JoinWelcome.svelte";

function arrive(url: string, scopes: string[]) {
  state.page.url = new URL(url);
  state.page.data = { effectivePermissions: scopes, tenantSlug: "sam" };
}

beforeEach(() => {
  state.tenants = [{ slug: "sam", displayName: "Sam" }];
  state.replaceState.mockClear();
});

describe("JoinWelcome", () => {
  it("welcomes a new member by the tenant's name and strips the marker", async () => {
    arrive("http://sam.localhost/?welcome=1", ["glucose.read"]);

    render(JoinWelcome);

    await expect
      .element(page.getByText("You're now viewing Sam's data."))
      .toBeVisible();
    expect(state.replaceState).toHaveBeenCalledOnce();
    expect(String(state.replaceState.mock.calls[0][0])).toBe(
      "http://sam.localhost/"
    );
  });

  it("falls back to a nameless line when no tenant name is known, as for a guest", async () => {
    state.tenants = [];
    arrive("http://sam.localhost/?welcome=1", ["glucose.read"]);

    render(JoinWelcome);

    await expect
      .element(page.getByText("You're now viewing the data shared with you."))
      .toBeVisible();
  });

  it("shows nothing without the marker", async () => {
    arrive("http://sam.localhost/", ["glucose.read"]);

    render(JoinWelcome);

    await expect
      .element(page.getByTestId("join-welcome"))
      .not.toBeInTheDocument();
    expect(state.replaceState).not.toHaveBeenCalled();
  });

  it("never welcomes the owner, but still strips the marker", async () => {
    arrive("http://sam.localhost/?welcome=1", ["*"]);

    render(JoinWelcome);

    await vi.waitFor(() => expect(state.replaceState).toHaveBeenCalledOnce());
    await expect
      .element(page.getByTestId("join-welcome"))
      .not.toBeInTheDocument();
  });

  it("dismisses", async () => {
    arrive("http://sam.localhost/?welcome=1", ["glucose.read"]);

    render(JoinWelcome);

    await page.getByRole("button", { name: "Dismiss" }).click();
    await expect
      .element(page.getByTestId("join-welcome"))
      .not.toBeInTheDocument();
  });
});
