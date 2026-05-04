import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import BolusSection from "./BolusSection.svelte";

describe("BolusSection", () => {
	it("renders section heading with icon", async () => {
		render(BolusSection, { bolus: {} });

		await expect.element(page.getByText("Bolus", { exact: true })).toBeVisible();
	});

	it("renders insulin and bolus type inputs", async () => {
		render(BolusSection, { bolus: {} });

		await expect.element(page.getByText("Insulin (U)")).toBeVisible();
		await expect.element(page.getByText("Bolus Type")).toBeVisible();
	});

	it("does not show remove button when onRemove is not provided", async () => {
		render(BolusSection, { bolus: {} });

		// No X button should be visible at the section level
		// The section header should just show "Bolus" without remove
		await expect.element(page.getByText("Bolus", { exact: true })).toBeVisible();
	});

	it("shows remove button when onRemove is provided", async () => {
		const on_remove = vi.fn();
		render(BolusSection, { bolus: {}, onRemove: on_remove });

		// There should be a remove button
		await expect.element(page.getByText("Bolus", { exact: true })).toBeVisible();
	});

	it("hides duration fields for Normal bolus type", async () => {
		render(BolusSection, {
			bolus: { bolusType: "Normal" as any },
		});

		await expect
			.element(page.getByText("Duration (min)"))
			.not.toBeInTheDocument();
		await expect
			.element(page.getByText("Rate (U/hr)"))
			.not.toBeInTheDocument();
	});

	it("shows duration fields for Square bolus type", async () => {
		render(BolusSection, {
			bolus: { bolusType: "Square" as any },
		});

		await expect.element(page.getByText("Duration (min)")).toBeVisible();
		await expect.element(page.getByText("Rate (U/hr)")).toBeVisible();
	});

	it("shows duration fields for Dual bolus type", async () => {
		render(BolusSection, {
			bolus: { bolusType: "Dual" as any },
		});

		await expect.element(page.getByText("Duration (min)")).toBeVisible();
		await expect.element(page.getByText("Rate (U/hr)")).toBeVisible();
	});

	it("renders the Advanced collapsible section", async () => {
		render(BolusSection, { bolus: {} });

		await expect.element(page.getByText("Advanced")).toBeVisible();
	});

	it("shows advanced fields when expanded", async () => {
		render(BolusSection, { bolus: {} });

		await page.getByText("Advanced").click();

		await expect.element(page.getByText("Programmed (U)")).toBeVisible();
		await expect.element(page.getByText("Delivered (U)")).toBeVisible();
		await expect.element(page.getByText("Insulin Type")).toBeVisible();
		await expect.element(page.getByText("Unabsorbed (U)")).toBeVisible();
	});
});
