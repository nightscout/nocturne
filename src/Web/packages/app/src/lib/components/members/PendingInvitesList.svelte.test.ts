import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import type { MemberInviteInfo } from "@nocturne/api-client";
import { formatMediumDate } from "#lib/utils/formatting.js";
import { accessEndInstant } from "./access-end.js";
import PendingInvitesList from "./PendingInvitesList.svelte";

const invite: MemberInviteInfo = {
  id: "33333333-3333-3333-3333-333333333333",
  label: "Camp session",
  roleNames: ["Caretaker"],
  expiresAt: "2099-01-01T00:00:00Z",
  useCount: 0,
  maxUses: 1,
};

describe("PendingInvitesList", () => {
  it("shows the day the access an invite gives ends", async () => {
    render(PendingInvitesList, {
      invites: [{ ...invite, accessEndsAt: accessEndInstant("2099-07-14") }],
      onRevoke: () => {},
      isRevoking: false,
    });

    await expect
      .element(page.getByText(`Access ends ${formatMediumDate(new Date(2099, 6, 14))}`, { exact: false }))
      .toBeVisible();
  });

  it("names the member who made each invite", async () => {
    render(PendingInvitesList, {
      invites: [
        { ...invite, createdByName: "Alex Carer" },
        { ...invite, id: "44444444-4444-4444-4444-444444444444", label: "Clinic", createdByName: "Sam Owner" },
      ],
      onRevoke: () => {},
      isRevoking: false,
    });

    await expect.element(page.getByText("Made by Alex Carer", { exact: false })).toBeVisible();
    await expect.element(page.getByText("Made by Sam Owner", { exact: false })).toBeVisible();
  });

  it("names the invite in the revoke button", async () => {
    render(PendingInvitesList, { invites: [invite], onRevoke: () => {}, isRevoking: false });

    await expect.element(page.getByRole("button", { name: "Revoke Camp session" })).toBeVisible();
  });

  it("says nothing of an end when the invite gives access with none", async () => {
    render(PendingInvitesList, { invites: [invite], onRevoke: () => {}, isRevoking: false });

    await expect.element(page.getByText("Camp session")).toBeVisible();
    expect(page.getByText("Access ends", { exact: false }).elements()).toHaveLength(0);
  });

  it("offers no revoke control and says why for an invite that outlasts the caller", async () => {
    render(PendingInvitesList, {
      invites: [{ ...invite, outlastsCaller: true }],
      onRevoke: () => {},
      isRevoking: false,
    });

    const reason = "This invite gives access that lasts longer than yours.";
    await expect.element(page.getByText(reason)).toBeVisible();
    await expect
      .element(page.getByRole("group", { name: "Camp session" }))
      .toHaveAccessibleDescription(reason);
    expect(page.getByRole("button", { name: "Revoke Camp session" }).elements()).toHaveLength(0);
  });

  it("offers revoke and no reason for an invite within reach", async () => {
    render(PendingInvitesList, {
      invites: [{ ...invite, outlastsCaller: false }],
      onRevoke: () => {},
      isRevoking: false,
    });

    await expect.element(page.getByRole("button", { name: "Revoke Camp session" })).toBeVisible();
    expect(
      page.getByText("This invite gives access that lasts longer than yours.").elements(),
    ).toHaveLength(0);
  });
});
