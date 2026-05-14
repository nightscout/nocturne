import { expect, test } from "@playwright/test";

test.describe("basal injection entry flow", () => {
	test("logs a long-acting basal injection end-to-end", async ({ page }) => {
		// Navigate to the treatments report page (where the Add Treatment action leads)
		await page.goto("/reports/treatments");

		// Wait for the page to be fully loaded
		await expect(page.getByRole("heading", { level: 1 })).toBeVisible({ timeout: 10_000 });

		// Open the treatment data table's add/edit dialog.
		// The TreatmentsDataTable should have a way to create a new entry.
		// Click the row or button that opens TreatmentEditDialog for a new basalInjection.
		// In the treatments page, there should be category tabs — click "Long-acting injection".
		const basalTab = page.getByRole("tab", { name: /long.acting injection/i });
		if (await basalTab.isVisible({ timeout: 3_000 })) {
			await basalTab.click();
		}

		// Look for an "Add" / "New" / "+" button to create a new treatment entry
		const addButton = page
			.getByRole("button", { name: /add|new|create/i })
			.first();
		await expect(addButton).toBeVisible({ timeout: 5_000 });
		await addButton.click();

		// The TreatmentEditDialog should open. If it presents an event type
		// selector, choose "Long-acting injection" (basalInjection category).
		const eventTypeSelect = page.getByText(/long.acting injection/i).first();
		if (await eventTypeSelect.isVisible({ timeout: 3_000 })) {
			await eventTypeSelect.click();
		}

		// --- BasalInjectionFormFields ---

		// Select a basal insulin from the dropdown.
		// The Select.Trigger shows "Select insulin..." as placeholder text.
		const insulinTrigger = page.getByText("Select insulin...");
		await expect(insulinTrigger).toBeVisible({ timeout: 5_000 });
		await insulinTrigger.click();

		// Wait for the dropdown content to appear, then pick the first option.
		// In a real environment this would be a configured patient insulin like "Tresiba".
		const firstInsulinOption = page
			.getByRole("option")
			.first();
		await expect(firstInsulinOption).toBeVisible({ timeout: 5_000 });
		const insulinName = await firstInsulinOption.textContent();
		await firstInsulinOption.click();

		// Enter units (20u)
		const unitsInput = page.locator("#basal-units");
		await expect(unitsInput).toBeVisible();
		await unitsInput.fill("20");

		// Optionally add a note
		const notesField = page.locator("#basal-notes");
		if (await notesField.isVisible({ timeout: 1_000 })) {
			await notesField.fill("Evening dose");
		}

		// Submit the form — the Save button in the dialog footer
		const saveButton = page.getByRole("button", { name: /save/i });
		await expect(saveButton).toBeEnabled();

		// Intercept the SvelteKit form action response (remote functions submit to the page route)
		const saveResponsePromise = page.waitForResponse(
			(resp) =>
				resp.url().includes("/reports/treatments") &&
				resp.request().method() === "POST",
			{ timeout: 10_000 },
		);

		await saveButton.click();

		// Wait for the API response to confirm the save went through
		const saveResponse = await saveResponsePromise;
		expect(saveResponse.ok()).toBe(true);

		// The dialog should close after a successful save
		await expect(page.locator("#basal-units")).not.toBeVisible({
			timeout: 5_000,
		});

		// The treatments table should now show the new basal injection entry.
		// RecentTreatmentsCard / TreatmentsDataTable renders "20u basal" for
		// basalInjection entries, and the insulin name as detail text.
		const newRow = page.getByText("20u basal");
		await expect(newRow).toBeVisible({ timeout: 10_000 });

		// The insulin name should appear alongside the entry
		if (insulinName) {
			const trimmedName = insulinName.trim().split("\n")[0].trim();
			if (trimmedName) {
				await expect(
					page.getByText(trimmedName).first(),
				).toBeVisible({ timeout: 5_000 });
			}
		}
	});
});
