import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";

vi.mock("#lib/api/generated/flagRules.generated.remote.js", async () => {
  const fixtures = await import("#lib/components/people/flag-rule-fixtures.js");
  const read = {
    current: { rules: [], limit: 20 },
    error: undefined,
    refresh: async () => {},
  };
  return {
    getFlagRules: () => read,
    getFlagRuleFacts: () => ({
      current: { presets: fixtures.FLAG_PRESETS, facts: fixtures.FLAG_FACTS },
      error: undefined,
      refresh: async () => {},
    }),
    createFlagRule: async () => ({}),
    updateFlagRule: async () => ({}),
    deleteFlagRule: async () => {},
    getTeamFlagRules: () => read,
    createTeamFlagRule: async () => ({}),
    updateTeamFlagRule: async () => ({}),
    deleteTeamFlagRule: async () => {},
  };
});

import RulesPage from "./+page.svelte";

describe("the viewer's own flag rules page", () => {
  beforeEach(() => {
    pageState.data = {};
  });

  it("lets the viewer add a rule", async () => {
    render(RulesPage);

    await expect
      .element(page.getByRole("button", { name: "Add a rule" }))
      .toBeEnabled();
    expect(
      page.getByText("You cannot change flag rules in the demo.").elements()
    ).toHaveLength(0);
  });

  it("lets the clinic persona add a rule, which the demo allows", async () => {
    pageState.data = {
      refusedAsDemoSubject: true,
      refusedStaffWritesAsDemoSubject: false,
      demoPersona: "clinic",
    };
    render(RulesPage);

    await expect
      .element(page.getByRole("button", { name: "Add a rule" }))
      .toBeEnabled();
  });

  it("offers no rule to add, and says why, where the demo refuses the staff writes", async () => {
    pageState.data = {
      refusedAsDemoSubject: true,
      refusedStaffWritesAsDemoSubject: true,
      demoPersona: null,
    };
    render(RulesPage);

    await expect
      .element(page.getByText("You cannot change flag rules in the demo."))
      .toBeVisible();
    expect(
      page.getByRole("button", { name: "Add a rule" }).elements()
    ).toHaveLength(0);
  });
});
