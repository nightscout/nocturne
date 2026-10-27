import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { error } from "@sveltejs/kit";
import { page } from "$app/state";
import type { TenantSettingsDto } from "#lib/api/generated/nocturne-api-client.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const mocks = vi.hoisted(() => ({
  getTenantSettings: vi.fn(),
  setDisplayName: vi.fn(),
  setPublicDocs: vi.fn(),
}));

const settings: TenantSettingsDto = {
  allowPublicDocs: false,
  slug: "sam",
  displayName: "Sam",
  sysCreatedAt: "2026-01-01T00:00:00Z",
};

vi.mock("#lib/api/generated/tenantSettings.generated.remote.js", () => ({
  getTenantSettings: mocks.getTenantSettings,
  setDisplayName: mocks.setDisplayName,
  setPublicDocs: mocks.setPublicDocs,
}));

import SitePage from "./+page.svelte";

describe("site details page", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    page.data.effectivePermissions = ["tenant.settings"];
    page.data.refusedAsDemoSubject = undefined;
    mocks.getTenantSettings.mockImplementation(() => remoteQuery(() => settings));
    mocks.setDisplayName.mockResolvedValue({ ...settings, displayName: "Sam's site" });
  });

  it("does not let the demo visitor rename the site", async () => {
    page.data.refusedAsDemoSubject = true;
    render(SitePage);

    await expect.element(browserPage.getByLabelText("Site name")).toBeDisabled();
    await expect
      .element(browserPage.getByText("You cannot change the name of the demo site."))
      .toBeVisible();
  });

  it("offers the API reference switch with the site's other settings", async () => {
    render(SitePage);

    await expect.element(browserPage.getByRole("switch", { name: "API reference" })).toBeVisible();
  });

  it("shows the name, the slug and the created date", async () => {
    render(SitePage);

    await expect.element(browserPage.getByLabelText("Site name")).toHaveValue("Sam");
    await expect.element(browserPage.getByText("sam", { exact: true })).toBeVisible();
  });

  it("saves the trimmed name", async () => {
    render(SitePage);

    await browserPage.getByLabelText("Site name").fill("  Sam's site ");
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect.element(browserPage.getByText("Site name saved.")).toBeVisible();
    expect(mocks.setDisplayName).toHaveBeenCalledWith({ displayName: "Sam's site" });
  });

  it("does not save a blank name", async () => {
    render(SitePage);

    await browserPage.getByLabelText("Site name").fill("   ");

    await expect.element(browserPage.getByText("Enter a site name.")).toBeVisible();
    await expect.element(browserPage.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(mocks.setDisplayName).not.toHaveBeenCalled();
  });

  it("reports a refused save", async () => {
    mocks.setDisplayName.mockImplementation(async () => error(400, "The name is too long."));
    render(SitePage);

    await browserPage.getByLabelText("Site name").fill("Other");
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect
      .element(browserPage.getByRole("alert"))
      .toHaveTextContent("The name is too long.");
  });

  it("tells a member without tenant.settings that they cannot see the details", async () => {
    page.data.effectivePermissions = ["glucose.read"];
    render(SitePage);

    await expect
      .element(browserPage.getByText("You cannot see the site details"))
      .toBeVisible();
    expect(mocks.getTenantSettings).not.toHaveBeenCalled();
  });
});
