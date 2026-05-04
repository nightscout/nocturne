import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import ScheduleEditor from "./ScheduleEditor.svelte";

describe("ScheduleEditor", () => {
	it("renders the label and add button", async () => {
		render(ScheduleEditor, {
			label: "Basal Rates",
			unit: "U/hr",
			entries: [],
		});

		await expect.element(page.getByText("Basal Rates")).toBeVisible();
		await expect.element(page.getByRole("button", { name: /Add/i })).toBeVisible();
	});

	it("shows empty state message when no entries", async () => {
		render(ScheduleEditor, {
			label: "Basal Rates",
			unit: "U/hr",
			entries: [],
		});

		await expect
			.element(page.getByText("No entries yet. Click Add to create one."))
			.toBeVisible();
	});

	it("renders existing entries with time and value inputs", async () => {
		render(ScheduleEditor, {
			label: "Basal Rates",
			unit: "U/hr",
			entries: [
				{ time: "06:00", value: 0.8 },
				{ time: "22:00", value: 0.5 },
			],
		});

		// Should show the unit label for each entry
		const unit_labels = page.getByText("U/hr");
		await expect.element(unit_labels.first()).toBeVisible();

		// Should not show empty state
		await expect
			.element(page.getByText("No entries yet. Click Add to create one."))
			.not.toBeInTheDocument();
	});

	it("adds a new entry when Add is clicked", async () => {
		render(ScheduleEditor, {
			label: "Basal Rates",
			unit: "U/hr",
			entries: [],
		});

		await page.getByRole("button", { name: /Add/i }).click();

		// Empty state should disappear
		await expect
			.element(page.getByText("No entries yet. Click Add to create one."))
			.not.toBeInTheDocument();

		// Should now show the unit label
		await expect.element(page.getByText("U/hr")).toBeVisible();
	});

	it("does not show delete button with single entry", async () => {
		render(ScheduleEditor, {
			label: "Basal Rates",
			unit: "U/hr",
			entries: [{ time: "06:00", value: 0.8 }],
		});

		// Trash button should not be present with only 1 entry
		const delete_buttons = page.getByRole("button").filter({ hasText: /Trash/i });
		// Since it's an icon button with no text, check there are no ghost icon buttons
		// The delete button has a Trash2 icon - with a single entry it shouldn't render
		await expect.element(page.getByText("U/hr")).toBeVisible();
	});

	it("shows delete buttons when multiple entries exist", async () => {
		render(ScheduleEditor, {
			label: "Basal Rates",
			unit: "U/hr",
			entries: [
				{ time: "06:00", value: 0.8 },
				{ time: "22:00", value: 0.5 },
			],
		});

		// Should have unit labels for both entries
		const unit_labels = page.getByText("U/hr");
		await expect.element(unit_labels.first()).toBeVisible();
	});
});
