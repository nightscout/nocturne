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

  it("names the invite in the revoke button", async () => {
    render(PendingInvitesList, { invites: [invite], onRevoke: () => {}, isRevoking: false });

    await expect.element(page.getByRole("button", { name: "Revoke Camp session" })).toBeVisible();
  });

  it("says nothing of an end when the invite gives access with none", async () => {
    render(PendingInvitesList, { invites: [invite], onRevoke: () => {}, isRevoking: false });

    await expect.element(page.getByText("Camp session")).toBeVisible();
    expect(page.getByText("Access ends", { exact: false }).elements()).toHaveLength(0);
  });
});
