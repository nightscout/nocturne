import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { error } from "@sveltejs/kit";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import {
  TenantCreationInviteStatus,
  type TenantCreationInviteOverview,
} from "#lib/api/generated/nocturne-api-client.js";

const mocks = vi.hoisted(() => ({
  overview: undefined as unknown as TenantCreationInviteOverview,
  allowOwnerSlugChange: false,
  create: vi.fn(),
  revoke: vi.fn(),
  setPolicy: vi.fn(),
}));

vi.mock("#lib/api/generated/tenantCreationInviteAdmins.generated.remote.js", () => ({
  getOverview: () => remoteQuery(() => mocks.overview),
  create: (arg: unknown) => remoteCommand(mocks.create)(arg),
  revoke: (arg: unknown) => remoteCommand(mocks.revoke)(arg),
}));

vi.mock("#lib/api/generated/platformPolicies.generated.remote.js", () => ({
  getPolicy: () => remoteQuery(() => ({ allowOwnerSlugChange: mocks.allowOwnerSlugChange })),
  setPolicy: (arg: unknown) => remoteCommand(mocks.setPolicy)(arg),
}));

import InvitesPage from "./+page.svelte";

const soleTenantNotice = {
  tenantSlug: "rhys",
  tenantDisplayName: "Rhys",
  bareAddress: "https://example.org",
  tenantAddress: "https://rhys.example.org",
};

describe("invite people page", () => {
  beforeEach(() => {
    mocks.overview = { invites: [], baseDomain: "example.org", subdomainNotice: undefined };
    mocks.allowOwnerSlugChange = false;
    mocks.create.mockReset();
    mocks.revoke.mockReset();
    mocks.setPolicy.mockReset();
  });

  it("explains how an invite works, including administrator access", async () => {
    render(InvitesPage);

    await expect.element(page.getByText("How an invite works")).toBeVisible();
    await expect
      .element(page.getByText("you can open their site for support", { exact: false }))
      .toBeVisible();
  });

  it("gates the first link on moving apps off the bare domain while one site exists", async () => {
    mocks.overview = { ...mocks.overview, subdomainNotice: soleTenantNotice };
    mocks.create.mockResolvedValue({ invite: {}, url: "https://example.org/start?token=abc" });
    render(InvitesPage);

    await expect
      .element(page.getByText("WARNING: Move your apps to the site address before you invite people"))
      .toBeVisible();
    const make = page.getByRole("button", { name: "Make link" });
    await expect.element(make).toBeDisabled();

    await page.getByRole("checkbox").click();
    await make.click();

    await vi.waitFor(() =>
      expect(mocks.create).toHaveBeenCalledWith(
        expect.objectContaining({ maxUses: 1, expiresInDays: 7, confirmSubdomainChange: true }),
      ),
    );
    await expect.element(page.getByText("Your link is ready")).toBeVisible();
    await expect.element(page.getByRole("textbox").last()).toHaveValue("https://example.org/start?token=abc");
  });

  it("makes a multi-use link with a cap, and refuses a cap below two", async () => {
    mocks.create.mockResolvedValue({ invite: {}, url: "https://example.org/start?token=xyz" });
    render(InvitesPage);

    await page.getByLabelText("More than one person").click();
    await page.getByLabelText("Maximum number of sites (optional)").fill("1");
    await expect.element(page.getByText("Enter a whole number of 2 or more.")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Make link" })).toBeDisabled();

    await page.getByLabelText("Maximum number of sites (optional)").fill("5");
    await page.getByLabelText("30 days").click();
    await page.getByRole("button", { name: "Make link" }).click();

    await vi.waitFor(() =>
      expect(mocks.create).toHaveBeenCalledWith(expect.objectContaining({ maxUses: 5, expiresInDays: 30 })),
    );
  });

  it("does not make links without a base domain", async () => {
    mocks.overview = { ...mocks.overview, baseDomain: undefined };
    render(InvitesPage);

    await expect.element(page.getByText("Set a base domain first")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Make link" })).toBeDisabled();
  });

  it("reports a refused link", async () => {
    mocks.create.mockImplementation(async () => error(400, "Expiry must be between 1 and 90 days."));
    render(InvitesPage);

    await page.getByRole("button", { name: "Make link" }).click();

    await expect.element(page.getByText("Expiry must be between 1 and 90 days.")).toBeVisible();
  });

  it("lists links with their uses, and cancels an active one", async () => {
    mocks.overview = {
      ...mocks.overview,
      invites: [
        {
          id: "11111111-1111-1111-1111-111111111111",
          label: "For Sam",
          maxUses: 1,
          useCount: 1,
          expiresAt: "2026-12-01T00:00:00Z",
          status: TenantCreationInviteStatus.UsedUp,
          uses: [{ slug: "sam", displayName: "Sam's glucose", address: "https://sam.example.org", redeemedAt: "2026-11-13T00:00:00Z" }],
        },
        {
          id: "22222222-2222-2222-2222-222222222222",
          label: "Clinic",
          maxUses: undefined,
          useCount: 0,
          expiresAt: "2026-12-01T00:00:00Z",
          status: TenantCreationInviteStatus.Active,
          uses: [],
        },
      ],
    };
    mocks.revoke.mockResolvedValue({ success: true });
    render(InvitesPage);

    await expect.element(page.getByText("For Sam")).toBeVisible();
    await expect.element(page.getByText("1 of 1 used", { exact: false })).toBeVisible();
    await expect.element(page.getByRole("link", { name: "Sam's glucose" })).toHaveAttribute("href", "https://sam.example.org");
    await expect.element(page.getByText("0 used, no limit", { exact: false })).toBeVisible();

    await page.getByRole("button", { name: "Cancel link" }).click();
    await vi.waitFor(() => expect(mocks.revoke).toHaveBeenCalledWith("22222222-2222-2222-2222-222222222222"));
  });

  it("turns the owner address-change policy on", async () => {
    mocks.setPolicy.mockResolvedValue({ allowOwnerSlugChange: true });
    render(InvitesPage);

    await page.getByRole("switch", { name: "Owners can change the address of their site" }).click();

    await vi.waitFor(() => expect(mocks.setPolicy).toHaveBeenCalledWith({ allowOwnerSlugChange: true }));
  });
});
