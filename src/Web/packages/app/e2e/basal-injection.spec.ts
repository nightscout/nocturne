import { expect, test } from "@playwright/test";

test.describe("basal injection entry flow", () => {
	test("logs a long-acting basal injection end-to-end", async ({ page }) => {
		// The treatments report page is where manual treatment creation lives.
		await page.goto("/reports/treatments");

		// Wait for the page to be fully loaded.
		await expect(page.getByRole("heading", { level: 1 })).toBeVisible({ timeout: 10_000 });

		// Open the "Add Treatment" kind picker and choose the long-acting injection.
		await page.getByRole("button", { name: /add treatment/i }).click();
		await page
			.getByRole("menuitem", { name: /long.acting injection/i })
			.click();

		// --- BasalInjectionFormFields (inside TreatmentEditDialog) ---

		// Select a basal insulin from the dropdown. The Select.Trigger shows
		// "Select insulin..." as placeholder text until one is chosen.
		const insulinTrigger = page.getByText("Select insulin...");
		await expect(insulinTrigger).toBeVisible({ timeout: 5_000 });
		await insulinTrigger.click();

		// Pick the first available basal insulin option (e.g. a configured "Tresiba").
		const firstInsulinOption = page.getByRole("option").first();
		await expect(firstInsulinOption).toBeVisible({ timeout: 5_000 });
		const insulinName = await firstInsulinOption.textContent();
		await firstInsulinOption.click();

		// Enter units (20U).
		const unitsInput = page.locator("#basal-units");
		await expect(unitsInput).toBeVisible();
		await unitsInput.fill("20");

		// Optionally add a note.
		const notesField = page.locator("#basal-notes");
		if (await notesField.isVisible({ timeout: 1_000 })) {
			await notesField.fill("Evening dose");
		}

		// Submit via the dialog's create button.
		const createButton = page.getByRole("button", { name: /create record/i });
		await expect(createButton).toBeEnabled();
		await createButton.click();

		// The dialog should close after a successful save.
		await expect(page.locator("#basal-units")).not.toBeVisible({
			timeout: 10_000,
		});

		// The treatments table should now show the new basal injection entry.
		// TreatmentsDataTable renders the units (e.g. "20U") in the value column,
		// with the insulin name as detail text.
		await expect(page.getByText("20U").first()).toBeVisible({ timeout: 10_000 });

		// The insulin name should appear alongside the entry.
		if (insulinName) {
			const trimmedName = insulinName.trim().split("\n")[0].trim();
			if (trimmedName) {
				await expect(page.getByText(trimmedName).first()).toBeVisible({
					timeout: 5_000,
				});
			}
		}
	});
});
