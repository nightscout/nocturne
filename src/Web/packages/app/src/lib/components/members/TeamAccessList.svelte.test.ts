import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import type { TenantTeamAccess } from "@nocturne/api-client";

vi.mock("#lib/api/generated/memberInvites.generated.remote.js", () => ({
  approveTeamStaff: vi.fn(),
  declineTeamStaff: vi.fn(),
  setTeamStaffRule: vi.fn(),
  editTeamAccess: vi.fn(),
}));

import TeamAccessList from "./TeamAccessList.svelte";

const clinicId = "66666666-6666-6666-6666-666666666666";
const nightsId = "88888888-8888-8888-8888-888888888888";
const team = (teamId: string, teamName: string): TenantTeamAccess => ({
  teamId,
  teamName,
  roles: [],
  limitTo24Hours: false,
  hasEnded: false,
  givenAt: "2026-11-01T00:00:00Z",
  teamManagesStaff: true,
  staff: [],
});

function props(teams: TenantTeamAccess[], linkedTeamId: string | null) {
  return {
    teams,
    canStop: true,
    canDecide: false,
    roles: [],
    rolesFailed: false,
    onRetryRoles: () => {},
    limitForced: false,
    linkedTeamId,
    onStop: () => {},
    onStale: () => {},
    onSuccess: () => {},
  };
}

describe("TeamAccessList link from a notice", () => {
  it("does not move the focus again when the list is read again for the same team", async () => {
    const screen = render(TeamAccessList, props([team(clinicId, "Clinic"), team(nightsId, "Nights")], nightsId));
    await expect.element(page.getByRole("group", { name: "Nights" })).toHaveFocus();
    const elsewhere = page.getByRole("button", { name: "Stop the access of Clinic" });
    (elsewhere.element() as HTMLElement).focus();
    const scrolled = vi.spyOn(Element.prototype, "scrollIntoView");

    await screen.rerender(props([team(clinicId, "Clinic"), team(nightsId, "Nights")], nightsId));

    await expect.element(page.getByRole("group", { name: "Nights" })).toBeVisible();
    await expect.element(elsewhere).toHaveFocus();
    expect(scrolled).not.toHaveBeenCalled();
    scrolled.mockRestore();
  });

  it("focuses the team of a new link after the first", async () => {
    const screen = render(TeamAccessList, props([team(clinicId, "Clinic"), team(nightsId, "Nights")], nightsId));
    await expect.element(page.getByRole("group", { name: "Nights" })).toHaveFocus();

    await screen.rerender(props([team(clinicId, "Clinic"), team(nightsId, "Nights")], clinicId));

    await expect.element(page.getByRole("group", { name: "Clinic" })).toHaveFocus();
  });
});
