import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { page as pageState } from "$app/state";
import type { FlagRuleSummary, TeamDetail } from "@nocturne/api-client";
import {
  FlagRuleRequestSchema,
  FlagRulesResponseSchema,
  FlagRuleSchema,
  TenantOverviewRuleReasonSchema,
} from "@nocturne/api-client/schemas";
import { FlagRuleSource } from "@nocturne/api-client";
import { makeRule, makeRuleReason } from "#lib/components/people/flag-rule-fixtures.js";

const mocks = vi.hoisted(() => ({
  team: undefined as unknown as TeamDetail,
  rules: [] as FlagRuleSummary[],
  teamReads: [] as string[],
  createTeamFlagRule: vi.fn(async (_args: unknown) => ({})),
}));

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({
  getTeam: () => ({
    get current() {
      return mocks.team;
    },
    error: undefined,
    loading: false,
    ready: true,
  }),
  removeStaff: vi.fn(),
  updateStaff: vi.fn(),
  createStaffInvite: vi.fn(),
  revokeStaffInvite: vi.fn(),
  createAccessLink: vi.fn(),
  revokeAccessLink: vi.fn(),
  renameTeam: vi.fn(),
  removeManager: vi.fn(),
  createManagerInvite: vi.fn(),
  revokeManagerInvite: vi.fn(),
  deleteTeam: vi.fn(),
  createGroup: vi.fn(),
  renameGroup: vi.fn(),
  deleteGroup: vi.fn(),
  addGroupPerson: vi.fn(),
  removeGroupPerson: vi.fn(),
  addGroupStaff: vi.fn(),
  removeGroupStaff: vi.fn(),
  setStaffReach: vi.fn(),
}));

vi.mock("#lib/api/generated/flagRules.generated.remote.js", async () => {
  const fixtures = await import("#lib/components/people/flag-rule-fixtures.js");
  return {
    getTeamFlagRules: (teamId: string) => {
      mocks.teamReads.push(teamId);
      return {
        get current() {
          return { rules: mocks.rules, limit: 20 };
        },
        error: undefined,
        refresh: async () => {},
      };
    },
    getFlagRuleFacts: () => ({
      current: { presets: fixtures.FLAG_PRESETS, facts: fixtures.FLAG_FACTS },
      error: undefined,
      refresh: async () => {},
    }),
    getFlagRules: vi.fn(),
    createFlagRule: vi.fn(),
    updateFlagRule: vi.fn(),
    deleteFlagRule: vi.fn(),
    createTeamFlagRule: mocks.createTeamFlagRule,
    updateTeamFlagRule: vi.fn(),
    deleteTeamFlagRule: vi.fn(),
  };
});

vi.mock("$app/navigation", () => ({ goto: vi.fn() }));

import TeamPage from "./+page.svelte";

const teamId = "11111111-1111-1111-1111-111111111111";
const ward = "0193b6a0-0000-7000-8000-0000000000b1";

const base: TeamDetail = {
  id: teamId,
  name: "Ward 4",
  managers: [{ subjectId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", displayName: "Alex" }],
  managerNames: ["Alex"],
  invites: [],
  staff: [],
  staffInvites: [],
  accessLinks: [],
  sites: [],
  groups: [],
  suggestableRoles: [],
  isLastManager: true,
  canDelete: false,
};

const manager: TeamDetail = { ...base, callerKind: "manager" };
const staff: TeamDetail = {
  ...base,
  callerKind: "staff",
  ownStaff: { narrowing: { limitTo24Hours: false }, hasEnded: false },
};

const section = () => page.getByTestId("team-flag-rules");

describe("the flag rules of a team on its page", () => {
  beforeEach(() => {
    pageState.params = { teamId };
    pageState.data = { user: { subjectId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" } };
    mocks.rules = [{ rule: makeRule(), cannotCheckCount: 1 }];
    mocks.teamReads = [];
    mocks.createTeamFlagRule.mockClear();
  });

  it("lets a manager add a rule from the suggested rules first, with every fact behind More facts", async () => {
    mocks.team = manager;
    render(TeamPage);
    await expect
      .element(section().getByTestId("flag-rule-sentence"))
      .toHaveTextContent("Time below consensus range in the last 14 days is above 4%");

    expect(mocks.teamReads).toContain(teamId);
    await expect.element(section().getByRole("button", { name: "Edit rule" })).toBeVisible();
    await expect.element(section().getByRole("button", { name: "Delete rule" })).toBeVisible();
    await section().getByRole("button", { name: "Add a rule" }).click();
    await expect.element(section().getByRole("button", { name: "More facts" })).toBeVisible();
    await section().getByTestId("flag-rule-preset").first().click();
    await section().getByRole("button", { name: "Save rule" }).click();

    expect(mocks.createTeamFlagRule).toHaveBeenCalledTimes(1);
    const { teamId: sentTo, request } = mocks.createTeamFlagRule.mock.calls[0][0] as { teamId: string; request: unknown };
    expect(sentTo).toBe(teamId);
    expect(FlagRuleRequestSchema.parse(request)).toEqual(request);
  });

  it("shows staff the team's rules, each with its level, its sentence and its count, and no control", async () => {
    mocks.team = staff;
    render(TeamPage);
    await expect.element(section().getByTestId("flag-rule-sentence")).toBeVisible();

    await expect
      .element(section().getByTestId("flag-rule-cannot-check"))
      .toHaveTextContent("Cannot be checked for 1 of the people this team follows that you can see");
    await expect.element(section().getByText("Warning:", { exact: true })).toBeInTheDocument();
    expect(section().getByRole("button").elements()).toHaveLength(0);
    await expect.element(section()).toHaveTextContent("Only a manager can change the rules.");
  });

  it("shows no rules to staff whose place in the team has ended", async () => {
    mocks.team = { ...staff, ownStaff: { narrowing: { endsAt: "2026-01-01T00:00:00Z" }, hasEnded: true } };
    render(TeamPage);
    await expect.element(page.getByRole("heading", { name: "Ward 4" })).toBeVisible();

    expect(section().elements()).toHaveLength(0);
    expect(mocks.teamReads).toEqual([]);
  });

  it("reads each body the team's rules come in through its generated schema", () => {
    const rule = FlagRuleSchema.parse(makeRule());
    const list = FlagRulesResponseSchema.parse({ rules: [{ rule, cannotCheckCount: 3 }], limit: 20 });

    expect(list).toEqual({ rules: [{ rule: makeRule(), cannotCheckCount: 3 }], limit: 20 });
    expect(FlagRuleSchema.safeParse({ ...makeRule(), teamId }).success).toBe(false);
    const reason = TenantOverviewRuleReasonSchema.parse(
      makeRuleReason({ source: FlagRuleSource.Team, team: { id: ward, name: "Ward 4" } })
    );
    expect(reason.team).toEqual({ id: ward, name: "Ward 4" });
  });
});
