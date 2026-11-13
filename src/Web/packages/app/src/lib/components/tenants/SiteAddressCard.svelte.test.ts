import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { error } from "@sveltejs/kit";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const mocks = vi.hoisted(() => ({
  subdomain: { slug: "sam", address: "https://sam.example.org", baseDomain: "example.org", canChange: true },
  changeSubdomain: vi.fn(),
  available: true,
}));

vi.mock("#lib/api/generated/tenantSettings.generated.remote.js", () => ({
  getSubdomain: () => remoteQuery(() => mocks.subdomain),
  changeSubdomain: (arg: unknown) => remoteCommand(mocks.changeSubdomain)(arg),
}));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  validateSlug: () =>
    remoteQuery(() => (mocks.available ? { isValid: true } : { isValid: false, message: "This name is already taken" })),
}));

import SiteAddressCard from "./SiteAddressCard.svelte";

async function openAndType(slug: string) {
  await page.getByRole("button", { name: "Change address" }).click();
  await page.getByLabelText("New subdomain").fill(slug);
}

describe("SiteAddressCard", () => {
  beforeEach(() => {
    mocks.subdomain = { slug: "sam", address: "https://sam.example.org", baseDomain: "example.org", canChange: true };
    mocks.available = true;
    mocks.changeSubdomain.mockReset();
  });

  it("shows the address and the subdomain", async () => {
    render(SiteAddressCard, { props: { createdAt: "2026-01-01T00:00:00Z" } });

    await expect.element(page.getByRole("textbox")).toHaveValue("https://sam.example.org");
    await expect.element(page.getByText("sam", { exact: true })).toBeVisible();
  });

  it("does not offer the change when the caller cannot make it", async () => {
    mocks.subdomain = { ...mocks.subdomain, canChange: false };
    render(SiteAddressCard);

    await expect.element(page.getByText("You cannot change the address of this site.")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Change address" })).not.toBeInTheDocument();
  });

  it("does not offer the change on the demo site", async () => {
    render(SiteAddressCard, { props: { isDemo: true } });

    await expect.element(page.getByRole("button", { name: "Change address" })).not.toBeInTheDocument();
  });

  it("warns what stops working, and changes only once the reader confirms", async () => {
    mocks.changeSubdomain.mockResolvedValue({ slug: "sam-taylor", canChange: true });
    render(SiteAddressCard);

    await openAndType("sam-taylor");
    await expect
      .element(page.getByText("WARNING: Apps that use the old address stop sending and showing data"))
      .toBeVisible();
    await expect.element(page.getByText("https://sam-taylor.example.org")).toBeVisible();
    await expect.element(page.getByText("This address is available.")).toBeVisible();

    const submit = page.getByRole("button", { name: "Change address" }).last();
    await expect.element(submit).toBeDisabled();

    await page.getByRole("checkbox").click();
    await submit.click();

    await vi.waitFor(() => expect(mocks.changeSubdomain).toHaveBeenCalledWith({ slug: "sam-taylor" }));
  });

  it("does not let a taken address through", async () => {
    mocks.available = false;
    render(SiteAddressCard);

    await openAndType("dev");
    await page.getByRole("checkbox").click();

    await expect.element(page.getByText("This name is already taken")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Change address" }).last()).toBeDisabled();
  });

  it("reports a refused change", async () => {
    mocks.changeSubdomain.mockImplementation(async () => error(400, "This name is reserved"));
    render(SiteAddressCard);

    await openAndType("admin-site");
    await expect.element(page.getByText("This address is available.")).toBeVisible();
    await page.getByRole("checkbox").click();
    await page.getByRole("button", { name: "Change address" }).last().click();

    await expect.element(page.getByText("This name is reserved")).toBeVisible();
  });
});
