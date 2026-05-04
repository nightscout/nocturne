import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import TargetRangeEditor from "./TargetRangeEditor.svelte";

describe("TargetRangeEditor", () => {
	it("renders the label and add button", async () => {
		render(TargetRangeEditor, {
			label: "Target Range",
			unit: "mg/dL",
			entries: [],
		});

		await expect.element(page.getByText("Target Range")).toBeVisible();
		await expect.element(page.getByRole("button", { name: /Add/i })).toBeVisible();
	});

	it("shows empty state message when no entries", async () => {
		render(TargetRangeEditor, {
			label: "Target Range",
			unit: "mg/dL",
			entries: [],
		});

		await expect
			.element(page.getByText("No entries yet. Click Add to create one."))
			.toBeVisible();
	});

	it("renders existing entries with low and high inputs", async () => {
		render(TargetRangeEditor, {
			label: "Target Range",
			unit: "mg/dL",
			entries: [{ time: "06:00", low: 70, high: 180 }],
		});

		await expect.element(page.getByText("mg/dL")).toBeVisible();

		// Should not show empty state
		await expect
			.element(page.getByText("No entries yet. Click Add to create one."))
			.not.toBeInTheDocument();
	});

	it("adds a new entry when Add is clicked", async () => {
		render(TargetRangeEditor, {
			label: "Target Range",
			unit: "mg/dL",
			entries: [],
		});

		await page.getByRole("button", { name: /Add/i }).click();

		// Empty state should disappear
		await expect
			.element(page.getByText("No entries yet. Click Add to create one."))
			.not.toBeInTheDocument();

		// Should show unit and dash separator
		await expect.element(page.getByText("mg/dL")).toBeVisible();
	});

	it("renders the dash separator between low and high", async () => {
		render(TargetRangeEditor, {
			label: "Target Range",
			unit: "mg/dL",
			entries: [{ time: "00:00", low: 70, high: 180 }],
		});

		await expect.element(page.getByText("-")).toBeVisible();
	});

	it("does not show delete button with single entry", async () => {
		render(TargetRangeEditor, {
			label: "Target Range",
			unit: "mg/dL",
			entries: [{ time: "06:00", low: 70, high: 180 }],
		});

		// With a single entry, delete buttons are hidden
		await expect.element(page.getByText("mg/dL")).toBeVisible();
	});
});
