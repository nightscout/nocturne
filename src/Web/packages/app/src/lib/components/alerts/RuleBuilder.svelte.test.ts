import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import RuleBuilder from "./RuleBuilder.svelte";
import { defaultPayload, ensureCompositeRoot, type RulePurpose } from "./types";

function renderLow(purpose: RulePurpose) {
	const node = $state(ensureCompositeRoot(defaultPayload("threshold")));
	render(RuleBuilder, { props: { node, purpose } });
	return node.composite!.conditions[0];
}

describe("RuleBuilder clear margin", () => {
	it("offers a clear margin in the conditions that start the alert, and removes it", async () => {
		const leaf = renderLow("notify");

		await page.getByRole("button", { name: "Condition actions" }).click();
		await page.getByRole("menuitem", { name: "Add a clear margin" }).click();
		expect(leaf.threshold?.clear_margin).toBe(0);
		await expect.element(page.getByRole("spinbutton", { name: "Clear margin" })).toBeVisible();

		await page.getByRole("button", { name: "Condition actions" }).click();
		await page.getByRole("menuitem", { name: "Remove the clear margin" }).click();
		expect(leaf.threshold?.clear_margin).toBeUndefined();
		await expect.element(page.getByRole("spinbutton", { name: "Clear margin" })).not.toBeInTheDocument();
	});

	it("removes the margin when “for at least” is added, and offers none inside it", async () => {
		const leaf = renderLow("notify");
		leaf.threshold!.clear_margin = 5;

		await page.getByRole("button", { name: "Condition actions" }).click();
		await page.getByRole("menuitem", { name: "Add “for at least”" }).click();
		expect(leaf.threshold?.clear_margin).toBeUndefined();

		const actions = page.getByRole("button", { name: "Condition actions" });
		await expect.poll(() => actions.elements().length).toBe(1);
		await actions.click();
		await expect.element(page.getByRole("menuitem", { name: "Remove “for at least”" })).toBeVisible();
		await expect.element(page.getByRole("menuitem", { name: "Add a clear margin" })).not.toBeInTheDocument();
	});

	it("can remove a stored margin the input does not show, as on an exact-value comparison", async () => {
		const node = $state(ensureCompositeRoot({ type: "iob", iob: { operator: "==", value: 2, clear_margin: 1 } }));
		render(RuleBuilder, { props: { node, purpose: "notify" } });
		const leaf = node.composite!.conditions[0];

		await expect.element(page.getByRole("spinbutton", { name: "Clear margin" })).not.toBeInTheDocument();
		await page.getByRole("button", { name: "Condition actions" }).click();
		await page.getByRole("menuitem", { name: "Remove the clear margin" }).click();
		expect(leaf.iob?.clear_margin).toBeUndefined();
	});

	it("does not offer one in auto-resolve conditions, which read none", async () => {
		renderLow("resolve");

		await page.getByRole("button", { name: "Condition actions" }).click();
		await expect.element(page.getByRole("menuitem", { name: "Make it the opposite (NOT)" })).toBeVisible();
		await expect.element(page.getByRole("menuitem", { name: "Add a clear margin" })).not.toBeInTheDocument();
	});
});
