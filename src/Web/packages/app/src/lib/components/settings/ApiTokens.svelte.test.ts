import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { DirectGrantDto } from "$api";
import {
  fakeRemoteQuery,
  type FakeQuery,
} from "$lib/api/fake-remote-query.svelte";

/**
 * A remote `query()` can only be awaited when its proxy was built in a tracking
 * context, so a component that reloads its list from a click handler gets a
 * rejection instead of data — the create still succeeds on the server, and the
 * failed reload is what reaches the user. {@link fakeRemoteQuery} reproduces
 * that rule; a bare `Promise` stand-in cannot.
 */

let grants: DirectGrantDto[];
let listQuery: () => FakeQuery<DirectGrantDto[]>;
let createImpl: () => Promise<{ token?: string }>;
let revokeImpl: () => Promise<unknown>;

vi.mock("$lib/api/generated/directGrants.generated.remote", () => ({
  list: () => listQuery(),
  create: () => createImpl(),
  revoke: () => revokeImpl(),
}));

import ApiTokens from "./ApiTokens.svelte";

const LOAD_FAILURE = "Failed to load API tokens.";

const grant = (id: string, label: string): DirectGrantDto => ({
  id,
  label,
  scopes: ["glucose.read"],
  isLegacy: false,
});

beforeEach(() => {
  grants = [];
  createImpl = () => Promise.resolve({ token: "noc_created" });
  revokeImpl = () => Promise.resolve({ success: true });
  listQuery = fakeRemoteQuery(() => Promise.resolve(grants));
});

async function openCreateDialog() {
  const onCreateClose = vi.fn();

  render(ApiTokens, { createOpen: true, onCreateClose });

  await expect.element(page.getByText("Create API token")).toBeVisible();
  expect(onCreateClose).not.toHaveBeenCalled();

  return onCreateClose;
}

describe("ApiTokens", () => {
  it("reports the create dialog closing from its own footer", async () => {
    const onCreateClose = await openCreateDialog();

    await page.getByRole("button", { name: "Cancel" }).click();

    expect(onCreateClose).toHaveBeenCalledTimes(1);
  });

  it("reports it closing when the dialog dismisses itself", async () => {
    const onCreateClose = await openCreateDialog();

    await userEvent.keyboard("{Escape}");

    await expect
      .element(page.getByText("Create API token"))
      .not.toBeInTheDocument();
    expect(onCreateClose).toHaveBeenCalledTimes(1);
  });
});

describe("ApiTokens create", () => {
  it("reports no load failure when the token was created", async () => {
    render(ApiTokens, {
      createOpen: true,
      prefillLabel: "Home Assistant",
      prefillScopes: ["glucose.read"],
    });

    await page
      .getByRole("dialog")
      .getByRole("button", { name: "Create token" })
      .click();

    await expect.element(page.getByText("Token created")).toBeVisible();
    await expect.element(page.getByText(LOAD_FAILURE)).not.toBeInTheDocument();
  });

  it("lists the new token without a page reload", async () => {
    createImpl = () => {
      grants = [grant("g-new", "Home Assistant")];
      return Promise.resolve({ token: "noc_created" });
    };

    render(ApiTokens, {
      createOpen: true,
      prefillLabel: "Home Assistant",
      prefillScopes: ["glucose.read"],
    });

    await page
      .getByRole("dialog")
      .getByRole("button", { name: "Create token" })
      .click();
    await page.getByRole("button", { name: "Done" }).click();

    await expect.element(page.getByText("Home Assistant")).toBeVisible();
  });

  it("still surfaces a real create failure", async () => {
    createImpl = () =>
      Promise.reject({ status: 400, body: { message: "Label already used." } });

    render(ApiTokens, {
      createOpen: true,
      prefillLabel: "Home Assistant",
      prefillScopes: ["glucose.read"],
    });

    await page
      .getByRole("dialog")
      .getByRole("button", { name: "Create token" })
      .click();

    await expect.element(page.getByText("Label already used.")).toBeVisible();
  });
});

describe("ApiTokens revoke", () => {
  beforeEach(() => {
    grants = [grant("g-1", "Old uploader key")];
  });

  it("reports success without also reporting a load failure", async () => {
    render(ApiTokens);

    await expect.element(page.getByText("Old uploader key")).toBeVisible();

    // The revoke control is icon-only; it follows the card's "Create token".
    await page.getByRole("button").nth(1).click();
    await page.getByRole("button", { name: "Revoke" }).click();

    await expect.element(page.getByText("API token revoked.")).toBeVisible();
    await expect.element(page.getByText(LOAD_FAILURE)).not.toBeInTheDocument();
  });

  it("drops the token from the list without a page reload", async () => {
    revokeImpl = () => {
      grants = [];
      return Promise.resolve({ success: true });
    };

    render(ApiTokens);
    await expect.element(page.getByText("Old uploader key")).toBeVisible();

    await page.getByRole("button").nth(1).click();
    await page.getByRole("button", { name: "Revoke" }).click();

    await expect
      .element(page.getByText("Old uploader key"))
      .not.toBeInTheDocument();
  });

  it("keeps the reason when the revoke itself fails", async () => {
    revokeImpl = () =>
      Promise.reject({ status: 409, body: { message: "Already revoked." } });

    render(ApiTokens);
    await expect.element(page.getByText("Old uploader key")).toBeVisible();

    await page.getByRole("button").nth(1).click();
    await page.getByRole("button", { name: "Revoke" }).click();

    await expect.element(page.getByText("Already revoked.")).toBeVisible();
  });
});

describe("ApiTokens load failure", () => {
  it("does not claim there are no tokens when the list could not be loaded", async () => {
    listQuery = fakeRemoteQuery(() =>
      Promise.reject({ status: 500, body: { message: "boom" } })
    );

    render(ApiTokens);

    await expect.element(page.getByText(LOAD_FAILURE)).toBeVisible();
    await expect
      .element(page.getByText("No API tokens.", { exact: false }))
      .not.toBeInTheDocument();
  });
});
