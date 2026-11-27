import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FlagRuleComparison, NotificationUrgency, type FlagRuleSummary } from "@nocturne/api-client";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { FLAG_FACTS, FLAG_PRESETS, makeRule } from "./flag-rule-fixtures.js";

const state = vi.hoisted(() => ({
  rules: [] as unknown[],
  limit: 20,
  create: vi.fn(async (_request: unknown) => ({})),
  update: vi.fn(async (_args: unknown) => ({})),
  remove: vi.fn(async (_ruleId: unknown) => {}),
  teamReads: [] as string[],
  createTeam: vi.fn(async (_args: unknown) => ({})),
  updateTeam: vi.fn(async (_args: unknown) => ({})),
  removeTeam: vi.fn(async (_args: unknown) => {}),
}));

vi.mock("#lib/api/generated/flagRules.generated.remote.js", async () => {
  const fixtures = await import("./flag-rule-fixtures.js");
  return {
    getFlagRules: () => ({
      get current() {
        return { rules: state.rules, limit: state.limit };
      },
      error: undefined,
      refresh: async () => {},
    }),
    getFlagRuleFacts: () => ({
      current: { presets: fixtures.FLAG_PRESETS, facts: fixtures.FLAG_FACTS },
      error: undefined,
      refresh: async () => {},
    }),
    createFlagRule: state.create,
    updateFlagRule: state.update,
    deleteFlagRule: state.remove,
    getTeamFlagRules: (teamId: string) => {
      state.teamReads.push(teamId);
      return {
        get current() {
          return { rules: state.rules, limit: state.limit };
        },
        error: undefined,
        refresh: async () => {},
      };
    },
    createTeamFlagRule: state.createTeam,
    updateTeamFlagRule: state.updateTeam,
    deleteTeamFlagRule: state.removeTeam,
  };
});

import FlagRulesEditor from "./FlagRulesEditor.svelte";

const personal = { owner: { kind: "personal" as const }, canEdit: true };
const teamId = "0193b6a0-0000-7000-8000-0000000000b1";
const managing = { owner: { kind: "team" as const, teamId }, canEdit: true };
const staffing = { owner: { kind: "team" as const, teamId }, canEdit: false };

function summary(cannotCheckCount: number, overrides: Parameters<typeof makeRule>[0] = {}): FlagRuleSummary {
  return { rule: makeRule(overrides), cannotCheckCount };
}

describe("FlagRulesEditor", () => {
  beforeEach(() => {
    state.rules = [];
    state.limit = 20;
    state.create.mockClear();
    state.update.mockClear();
    state.remove.mockClear();
    state.teamReads = [];
    state.createTeam.mockClear();
    state.updateTeam.mockClear();
    state.removeTeam.mockClear();
    glucoseUnits.current = "mg/dl";
  });

  afterEach(() => {
    glucoseUnits.current = "mg/dl";
  });

  it("lists each rule as a sentence, and how many people it cannot be checked for", async () => {
    state.rules = [
      summary(3),
      summary(0, {
        id: "00000000-0000-7000-8000-000000000002",
        dataset: "current_status",
        field: "hoursSinceReading",
        days: undefined,
        value: 2,
      }),
    ];
    render(FlagRulesEditor, { props: personal });

    const sentences = page.getByTestId("flag-rule-sentence");
    await expect.element(sentences.first()).toHaveTextContent("Time below consensus range in the last 14 days is above 4%");
    await expect.element(sentences.nth(1)).toHaveTextContent("Hours since the last reading is above 2 h");
    const cannotCheck = page.getByTestId("flag-rule-cannot-check");
    await expect.element(cannotCheck).toHaveTextContent("Cannot be checked for 3 of the people you follow");
    expect(cannotCheck.elements()).toHaveLength(1);
  });

  it("opens on the suggested rules and saves the one picked at warn", async () => {
    render(FlagRulesEditor, { props: personal });
    await expect.element(page.getByTestId("flag-rules-empty")).toBeVisible();

    await page.getByRole("button", { name: "Add a rule" }).click();
    const presets = page.getByTestId("flag-rule-preset");
    await expect.element(presets.first()).toBeVisible();
    expect(presets.elements()).toHaveLength(FLAG_PRESETS.length);
    await presets.first().click();
    await page.getByRole("button", { name: "Save rule" }).click();

    expect(state.create).toHaveBeenCalledWith({
      dataset: "glucose_statistics",
      field: "timeBelow",
      days: 14,
      comparison: FlagRuleComparison.Above,
      value: 4,
      level: NotificationUrgency.Warn,
    });
  });

  it("shows every fact behind More facts, with its family and its unit", async () => {
    render(FlagRulesEditor, { props: personal });
    await page.getByRole("button", { name: "Add a rule" }).click();
    await expect.element(page.getByTestId("flag-rule-preset").first()).toBeVisible();
    expect(page.getByTestId("flag-rule-fact-picker").elements()).toHaveLength(0);

    await page.getByRole("button", { name: "More facts" }).click();
    await page.getByTestId("flag-rule-fact-picker").click();

    for (const fact of FLAG_FACTS)
      await expect.element(page.getByRole("option", { name: fact.field.label })).toBeVisible();
    await expect.element(page.getByRole("option", { name: "Time below own range" })).toHaveTextContent("Own range");
    await expect.element(page.getByRole("option", { name: "Time below consensus range" })).toHaveTextContent("Consensus range");
  });

  it("takes a glucose value in mmol/L and saves it in mg/dL", async () => {
    glucoseUnits.current = "mmol";
    render(FlagRulesEditor, { props: personal });
    await page.getByRole("button", { name: "Add a rule" }).click();
    await page.getByRole("button", { name: "More facts" }).click();
    await page.getByTestId("flag-rule-fact-picker").click();
    await page.getByRole("option", { name: "Mean glucose" }).click();

    await expect.element(page.getByTestId("flag-rule-unit")).toHaveTextContent("mmol/L");
    await page.getByRole("spinbutton", { name: "Value" }).fill("10");
    await page.getByRole("button", { name: "Save rule" }).click();

    expect(state.create).toHaveBeenCalledTimes(1);
    const request = state.create.mock.calls[0][0] as { field: string; days: number; value: number };
    expect(request.field).toBe("mean");
    expect(request.days).toBe(14);
    expect(request.value).toBeCloseTo(180, 0);
  });

  it("changes a rule in place, its glucose value shown in the viewer's unit", async () => {
    glucoseUnits.current = "mmol";
    const id = "00000000-0000-7000-8000-000000000009";
    state.rules = [summary(0, { id, field: "mean", value: 180 })];
    render(FlagRulesEditor, { props: personal });

    await page.getByRole("button", { name: "Edit rule" }).click();
    const value = page.getByRole("spinbutton", { name: "Value" });
    await expect.element(value).toBeVisible();
    expect(Number((value.element() as HTMLInputElement).value)).toBeCloseTo(10, 1);
    await value.fill("11");
    await page.getByRole("button", { name: "Save rule" }).click();

    expect(state.update).toHaveBeenCalledTimes(1);
    const args = state.update.mock.calls[0][0] as { ruleId: string; request: { value: number } };
    expect(args.ruleId).toBe(id);
    expect(args.request.value).toBeCloseTo(198, 0);
  });

  it("deletes a rule", async () => {
    const id = "00000000-0000-7000-8000-000000000007";
    state.rules = [summary(0, { id })];
    render(FlagRulesEditor, { props: personal });

    await page.getByRole("button", { name: "Delete rule" }).click();

    expect(state.remove).toHaveBeenCalledWith(id);
  });

  it("disables adding at the limit and says why", async () => {
    state.limit = 2;
    state.rules = [
      summary(0),
      summary(0, { id: "00000000-0000-7000-8000-000000000002" }),
    ];
    render(FlagRulesEditor, { props: personal });

    await expect.element(page.getByRole("button", { name: "Add a rule" })).toBeDisabled();
    await expect
      .element(page.getByTestId("flag-rules-limit"))
      .toHaveTextContent("You have 2 rules, the maximum. Delete a rule to add a new rule.");
  });

  it("allows adding below the limit with no limit sentence", async () => {
    state.rules = [summary(0)];
    render(FlagRulesEditor, { props: personal });

    await expect.element(page.getByRole("button", { name: "Add a rule" })).toBeEnabled();
    expect(page.getByTestId("flag-rules-limit").elements()).toHaveLength(0);
  });

  describe("a team's rules", () => {
    it("reads, adds, changes and deletes the team's rules for a manager", async () => {
      const id = "00000000-0000-7000-8000-000000000005";
      state.rules = [summary(0, { id })];
      render(FlagRulesEditor, { props: managing });
      await expect.element(page.getByTestId("flag-rule-sentence")).toBeVisible();

      expect(state.teamReads).toContain(teamId);
      await page.getByRole("button", { name: "Delete rule" }).click();
      expect(state.removeTeam).toHaveBeenCalledWith({ teamId, ruleId: id });

      await page.getByRole("button", { name: "Edit rule" }).click();
      await page.getByRole("spinbutton", { name: "Value" }).fill("5");
      await page.getByRole("button", { name: "Save rule" }).click();
      expect(state.updateTeam).toHaveBeenCalledWith({ teamId, ruleId: id, request: expect.objectContaining({ value: 5 }) });

      await page.getByRole("button", { name: "Add a rule" }).click();
      await page.getByTestId("flag-rule-preset").first().click();
      await page.getByRole("button", { name: "Save rule" }).click();
      expect(state.createTeam).toHaveBeenCalledWith({ teamId, request: expect.objectContaining({ field: "timeBelow", value: 4 }) });
      expect(state.create).not.toHaveBeenCalled();
    });

    it("lists the team's rules for staff with no control to change them", async () => {
      state.rules = [summary(2)];
      render(FlagRulesEditor, { props: staffing });

      await expect
        .element(page.getByTestId("flag-rule-sentence"))
        .toHaveTextContent("Time below consensus range in the last 14 days is above 4%");
      await expect
        .element(page.getByTestId("flag-rule-cannot-check"))
        .toHaveTextContent("Cannot be checked for 2 of the people this team follows that you can see");
      expect(page.getByRole("button").elements()).toHaveLength(0);
    });

    it("says the team has no rules", async () => {
      render(FlagRulesEditor, { props: staffing });

      await expect.element(page.getByTestId("flag-rules-empty")).toHaveTextContent("This team has no flag rules.");
    });

    it("states the team's limit when it is reached", async () => {
      state.limit = 2;
      state.rules = [summary(0), summary(0, { id: "00000000-0000-7000-8000-000000000002" })];
      render(FlagRulesEditor, { props: managing });

      await expect.element(page.getByRole("button", { name: "Add a rule" })).toBeDisabled();
      await expect
        .element(page.getByTestId("flag-rules-limit"))
        .toHaveTextContent("This team has 2 rules, the maximum. Delete a rule to add a new rule.");
    });
  });
});
