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

	// When activeDays is undefined, all days should be active (bg-primary)
	const mon = page.getByRole("button", { name: "Mon" });
	await expect.element(mon).toHaveClass(/bg-primary/);
});

test("marks selected days as active", async () => {
	render(DayOfWeekPicker, { activeDays: [1, 3] });

	// Mon (1) should be active
	await expect
		.element(page.getByRole("button", { name: "Mon" }))
		.toHaveClass(/bg-primary/);

	// Wed (3) should be active
	await expect
		.element(page.getByRole("button", { name: "Wed" }))
		.toHaveClass(/bg-primary/);

	// Fri (5) should NOT be active
	await expect
		.element(page.getByRole("button", { name: "Fri" }))
		.not.toHaveClass(/bg-primary/);
});

test("toggles a day on click", async () => {
	render(DayOfWeekPicker, { activeDays: [1, 3] });

	// Fri is not active initially
	const fri = page.getByRole("button", { name: "Fri" });
	await expect.element(fri).not.toHaveClass(/bg-primary/);

	await fri.click();

	// Fri should now have the active class
	await expect.element(fri).toHaveClass(/bg-primary/);
});

test("deactivates an active day on click", async () => {
	render(DayOfWeekPicker, { activeDays: [1, 3] });

	const mon = page.getByRole("button", { name: "Mon" });
	await expect.element(mon).toHaveClass(/bg-primary/);

	await mon.click();

	// Mon should no longer have the active class
	await expect.element(mon).not.toHaveClass(/bg-primary/);
});
