import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { formatLocale } from "#lib/utils/formatting.js";
import { GuestLinkStatus, type GuestLinkInfo } from "#lib/api/generated/nocturne-api-client.js";
import {
  remoteCommand,
  remoteQuery,
  updatedBy,
} from "#lib/test-stubs/remote-resource.js";

let createImpl: (request?: { scopes?: string[] }) => Promise<unknown>;
let revokeImpl: () => Promise<unknown>;
let dismissImpl: () => Promise<unknown>;
let links: GuestLinkInfo[];

vi.mock("$app/state", () => ({
  page: { data: { effectivePermissions: ["*"] } },
}));

vi.mock("#lib/api/generated/guestLinks.generated.remote.js", () => ({
  getGuestLinks: () => remoteQuery(() => links),
  getGuestLinkScopeOptions: () =>
    remoteQuery(() => ({
      available: ["alerts.read", "glucose.read", "therapy.read"],
      defaults: ["alerts.read", "glucose.read"],
    })),
  createGuestLink: remoteCommand((request) =>
    createImpl(request as { scopes?: string[] } | undefined)
  ),
  revokeGuestLink: remoteCommand(() => revokeImpl()),
  dismissGuestLink: remoteCommand(() => dismissImpl()),
}));

import {
  getGuestLinks,
  createGuestLink,
  revokeGuestLink,
  dismissGuestLink,
} from "#lib/api/generated/guestLinks.generated.remote.js";
import GuestLinksSection from "./GuestLinksSection.svelte";

const FALLBACK = "Failed to create guest link. Try again.";
const REISSUE_FALLBACK =
  "Failed to create a new code. Active links are limited to 5 at a time.";

const REVOKE_FALLBACK = "Failed to revoke the link. Try again.";
const DISMISS_FALLBACK = "Failed to dismiss the link. Try again.";

const existingLink: GuestLinkInfo = {
  id: "11111111-1111-1111-1111-111111111111",
  label: "Dr Smith",
  status: GuestLinkStatus.Active,
  scopes: ["reports.read"],
};

async function attemptCreate() {
  render(GuestLinksSection);

  await page.getByRole("button", { name: "Create guest link" }).click();
  await page.getByRole("textbox").fill("Dr Smith");
  await page.getByRole("button", { name: "Create link" }).click();
}

async function attemptReissue() {
  links = [existingLink];
  render(GuestLinksSection);

  await page.getByRole("button", { name: "New code" }).click();
}

describe("GuestLinksSection", () => {
  beforeEach(() => {
    links = [];
    revokeImpl = () => Promise.resolve();
    dismissImpl = () => Promise.resolve();
    createImpl = () =>
      Promise.resolve({ code: "ABCD", fullUrl: "/guest/ABCD" });
  });

  it("creates the link with the categories ticked by default", async () => {
    let sent: string[] | undefined;
    createImpl = async (request) => {
      sent = request?.scopes;
      return { code: "ABCD", fullUrl: "/guest/ABCD" };
    };

    await attemptCreate();

    await expect.element(page.getByText("ABCD")).toBeInTheDocument();
    expect(sent).toEqual(["alerts.read", "glucose.read"]);
  });

  it("creates the link with the categories the creator picks", async () => {
    let sent: string[] | undefined;
    createImpl = async (request) => {
      sent = request?.scopes;
      return { code: "ABCD", fullUrl: "/guest/ABCD" };
    };
    render(GuestLinksSection);

    await page.getByRole("button", { name: "Create guest link" }).click();
    await page.getByRole("textbox").fill("Daycare");
    await page.getByRole("switch", { name: "Alerts" }).click();
    await page.getByRole("switch", { name: "Therapy settings" }).click();
    await page.getByRole("button", { name: "Create link" }).click();

    await expect.element(page.getByText("ABCD")).toBeInTheDocument();
    expect(sent?.toSorted()).toEqual(["glucose.read", "therapy.read"]);
  });

  it("shows the server's reason when the code could not be issued", async () => {
    createImpl = async () => error(409, "You already have 5 active links.");

    await attemptCreate();

    await expect
      .element(page.getByText("You already have 5 active links."))
      .toBeInTheDocument();
  });

  it("keeps a server fault behind the section's own wording", async () => {
    createImpl = async () => error(500, "npgsql: connection reset");

    await attemptCreate();

    await expect.element(page.getByText(FALLBACK)).toBeInTheDocument();
    expect(page.getByText("npgsql: connection reset").elements()).toHaveLength(
      0
    );
  });

  it("reports a throttled attempt as throttled rather than as a failed create", async () => {
    createImpl = async () => error(429, "Too many requests");

    await attemptCreate();

    await expect
      .element(page.getByText("Too many attempts.", { exact: false }))
      .toBeInTheDocument();
    expect(page.getByText(FALLBACK).elements()).toHaveLength(0);
  });

  it("shows the server's reason when reissuing a code is refused", async () => {
    createImpl = async () =>
      error(409, "That guest has an unused code already.");

    await attemptReissue();

    await expect
      .element(page.getByText("That guest has an unused code already."))
      .toBeInTheDocument();
  });

  it("keeps a server fault behind the reissue wording", async () => {
    createImpl = async () => error(500, "npgsql: connection reset");

    await attemptReissue();

    await expect.element(page.getByText(REISSUE_FALLBACK)).toBeInTheDocument();
    expect(page.getByText("npgsql: connection reset").elements()).toHaveLength(
      0
    );
  });

  it("says the link could not be revoked when it is gone", async () => {
    revokeImpl = async () => error(404, "Not found");
    links = [existingLink];
    render(GuestLinksSection);

    await page.getByRole("button", { name: "Revoke" }).click();

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent(REVOKE_FALLBACK);
  });

  it("keeps a server fault behind the revoke wording", async () => {
    revokeImpl = async () => error(500, "npgsql: connection reset");
    links = [existingLink];
    render(GuestLinksSection);

    await page.getByRole("button", { name: "Revoke" }).click();

    await expect.element(page.getByText(REVOKE_FALLBACK)).toBeInTheDocument();
    expect(page.getByText("npgsql: connection reset").elements()).toHaveLength(
      0
    );
  });

  it("says the link could not be dismissed when it is gone", async () => {
    dismissImpl = async () => error(404, "Not found");
    links = [{ ...existingLink, status: GuestLinkStatus.Revoked }];
    render(GuestLinksSection);

    await page.getByRole("button", { name: "Dismiss" }).click();

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent(DISMISS_FALLBACK);
  });

  it("keeps a server fault behind the dismiss wording", async () => {
    dismissImpl = async () => error(500, "npgsql: connection reset");
    links = [{ ...existingLink, status: GuestLinkStatus.Revoked }];
    render(GuestLinksSection);

    await page.getByRole("button", { name: "Dismiss" }).click();

    await expect.element(page.getByText(DISMISS_FALLBACK)).toBeInTheDocument();
    expect(page.getByText("npgsql: connection reset").elements()).toHaveLength(
      0
    );
  });

  it("clears a revoke failure when the next revoke succeeds", async () => {
    revokeImpl = async () => error(404, "Not found");
    links = [existingLink];
    render(GuestLinksSection);
    await page.getByRole("button", { name: "Revoke" }).click();
    await expect.element(page.getByRole("alert")).toBeInTheDocument();

    revokeImpl = () => Promise.resolve();
    await page.getByRole("button", { name: "Revoke" }).click();

    await expect.poll(() => page.getByRole("alert").elements()).toHaveLength(0);
  });

  it("clears a dismiss failure when the next dismiss succeeds", async () => {
    dismissImpl = async () => error(404, "Not found");
    links = [{ ...existingLink, status: GuestLinkStatus.Revoked }];
    render(GuestLinksSection);
    await page.getByRole("button", { name: "Dismiss" }).click();
    await expect.element(page.getByRole("alert")).toBeInTheDocument();

    dismissImpl = () => Promise.resolve();
    await page.getByRole("button", { name: "Dismiss" }).click();

    await expect.poll(() => page.getByRole("alert").elements()).toHaveLength(0);
  });

  it("shows a refused re-issue beside the links while a created code is still on screen", async () => {
    links = [existingLink];
    render(GuestLinksSection);
    await page.getByRole("button", { name: "Create guest link" }).click();
    await page.getByRole("textbox").fill("Dr Smith");
    await page.getByRole("button", { name: "Create link" }).click();
    await expect.element(page.getByText("ABCD")).toBeInTheDocument();

    createImpl = async () =>
      error(409, "You already have 5 active links.");
    await page.getByRole("button", { name: "New code" }).click();

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("You already have 5 active links.");
  });

  it("words the expiry of a live link and of an expired one through the regional formatter", async () => {
    const narrow = new Intl.RelativeTimeFormat(formatLocale(), {
      style: "narrow",
      numeric: "auto",
    });
    const offset = 3 * 3_600_000 + 5 * 60_000;
    links = [
      {
        ...existingLink,
        id: "22222222-2222-2222-2222-222222222222",
        expiresAt: new Date(Date.now() + offset).toISOString() as never,
      },
      {
        ...existingLink,
        id: "33333333-3333-3333-3333-333333333333",
        status: GuestLinkStatus.Expired,
        expiresAt: new Date(Date.now() - offset).toISOString() as never,
      },
    ];
    render(GuestLinksSection);

    await expect
      .element(page.getByText(`Expires ${narrow.format(3, "hour")}`))
      .toBeInTheDocument();
    await expect
      .element(page.getByText(`Expired ${narrow.format(-3, "hour")}`))
      .toBeInTheDocument();
  });

  it("refreshes the guest links the section reads when a link is revoked", async () => {
    links = [existingLink];
    updatedBy(revokeGuestLink).length = 0;
    render(GuestLinksSection);

    await page.getByRole("button", { name: "Revoke" }).click();

    await vi.waitFor(() =>
      expect(updatedBy(revokeGuestLink)).toEqual([[getGuestLinks]])
    );
  });

  it("refreshes the guest links the section reads when a finished link is dismissed", async () => {
    links = [{ ...existingLink, status: GuestLinkStatus.Revoked }];
    updatedBy(dismissGuestLink).length = 0;
    render(GuestLinksSection);

    await page.getByRole("button", { name: "Dismiss" }).click();

    await vi.waitFor(() =>
      expect(updatedBy(dismissGuestLink)).toEqual([[getGuestLinks]])
    );
  });

  it("refreshes the guest links the section reads when a code is created", async () => {
    updatedBy(createGuestLink).length = 0;

    await attemptCreate();

    await vi.waitFor(() =>
      expect(updatedBy(createGuestLink)).toEqual([[getGuestLinks]])
    );
  });

  it("refreshes the guest links the section reads when a code is reissued", async () => {
    updatedBy(createGuestLink).length = 0;

    await attemptReissue();

    await vi.waitFor(() =>
      expect(updatedBy(createGuestLink)).toEqual([[getGuestLinks]])
    );
  });
});
