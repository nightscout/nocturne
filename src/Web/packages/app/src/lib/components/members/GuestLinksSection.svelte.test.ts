import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { GuestLinkStatus, type GuestLinkInfo } from "$api-clients";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

let createImpl: () => Promise<unknown>;
let links: GuestLinkInfo[];

vi.mock("$app/state", () => ({
  page: { data: { effectivePermissions: ["*"] } },
}));

vi.mock("$api/generated/guestLinks.generated.remote", () => ({
  getGuestLinks: () => remoteQuery(() => links),
  createGuestLink: () => createImpl(),
  revokeGuestLink: () => Promise.resolve(),
  dismissGuestLink: () => Promise.resolve(),
}));

import GuestLinksSection from "./GuestLinksSection.svelte";

const FALLBACK = "Failed to create guest link. Please try again.";
const REISSUE_FALLBACK =
  "Failed to create a new code. Active links are limited to 5 at a time.";

const existingLink: GuestLinkInfo = {
  id: "11111111-1111-1111-1111-111111111111",
  label: "Dr Smith",
  status: GuestLinkStatus.Active,
  scopes: ["reports.read"],
};

async function attemptCreate() {
  render(GuestLinksSection);

  await page.getByRole("button", { name: "Create Guest Link" }).click();
  await page.getByRole("textbox").fill("Dr Smith");
  await page.getByRole("button", { name: "Create Link" }).click();
}

async function attemptReissue() {
  links = [existingLink];
  render(GuestLinksSection);

  await page.getByRole("button", { name: "New code" }).click();
}

describe("GuestLinksSection", () => {
  beforeEach(() => {
    links = [];
    createImpl = () =>
      Promise.resolve({ code: "ABCD", fullUrl: "/guest/ABCD" });
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
});
