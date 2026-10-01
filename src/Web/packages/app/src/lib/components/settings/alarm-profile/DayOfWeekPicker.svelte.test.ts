import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { expect, test } from "vitest";
import DayOfWeekPicker from "./DayOfWeekPicker.svelte";

test("renders all seven days", async () => {
	render(DayOfWeekPicker);

	for (const day of ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"]) {
		await expect
			.element(page.getByRole("button", { name: day }))
			.toBeVisible();
	}
});

test("marks all days active when activeDays is undefined", async () => {
	render(DayOfWeekPicker);

	// When activeDays is undefined, all days should be pressed
	const mon = page.getByRole("button", { name: "Mon" });
	await expect.element(mon).toHaveAttribute("aria-pressed", "true");
});

test("marks selected days as active", async () => {
	render(DayOfWeekPicker, { activeDays: [1, 3] });

	// Mon (1) should be active
	await expect
		.element(page.getByRole("button", { name: "Mon" }))
		.toHaveAttribute("aria-pressed", "true");

	// Wed (3) should be active
	await expect
		.element(page.getByRole("button", { name: "Wed" }))
		.toHaveAttribute("aria-pressed", "true");

	// Fri (5) should NOT be active
	await expect
		.element(page.getByRole("button", { name: "Fri" }))
		.toHaveAttribute("aria-pressed", "false");
});

test("toggles a day on click", async () => {
	render(DayOfWeekPicker, { activeDays: [1, 3] });

	// Fri is not active initially
	const fri = page.getByRole("button", { name: "Fri" });
	await expect.element(fri).toHaveAttribute("aria-pressed", "false");

	await fri.click();

	// Fri should now be pressed
	await expect.element(fri).toHaveAttribute("aria-pressed", "true");
});

test("deactivates an active day on click", async () => {
	render(DayOfWeekPicker, { activeDays: [1, 3] });

	const mon = page.getByRole("button", { name: "Mon" });
	await expect.element(mon).toHaveAttribute("aria-pressed", "true");

	await mon.click();

	// Mon should no longer be pressed
	await expect.element(mon).toHaveAttribute("aria-pressed", "false");
});
