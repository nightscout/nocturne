import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import RuleBuilderLeafEditor from "./RuleBuilderLeafEditor.svelte";
import { buildBody, defaultPayload, parseRule } from "./types";
import { AlertConditionType } from "$api-clients";

describe("RuleBuilderLeafEditor is_active", () => {
	it("shows a stored leaf missing is_active as off, as the engines read it", async () => {
		const state = parseRule({
			name: "Stored",
			conditionType: AlertConditionType.PumpSuspended,
			conditionParams: { for_minutes: 30 },
		});
		const leaf = state.condition!.composite!.conditions[0];

		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await expect.element(page.getByRole("switch")).not.toBeChecked();
		await expect.element(page.getByText("inactive")).toBeVisible();
	});

	it("shows a new leaf as on, as it is stored", async () => {
		const leaf = defaultPayload("pump_state");

		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await expect.element(page.getByRole("switch")).toBeChecked();
		await expect.element(page.getByText("active", { exact: true })).toBeVisible();
		expect(leaf.pump_state?.is_active).toBe(true);
	});
});

describe("saving is_active", () => {
	it("saves false for a stored leaf missing it", () => {
		const state = parseRule({
			name: "Stored",
			conditionType: AlertConditionType.StateSpanActive,
			conditionParams: { category: "Override" },
		});
		expect(buildBody(state).conditionParams).toEqual({ category: "Override", is_active: false });
	});

	it("saves true for a new leaf", () => {
		const state = parseRule(null);
		state.condition!.composite!.conditions = [defaultPayload("sleep_session_active")];
		expect(buildBody(state).conditionParams).toEqual({ is_active: true });
	});
});
