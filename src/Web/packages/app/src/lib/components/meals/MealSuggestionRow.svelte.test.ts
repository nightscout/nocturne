import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import MealSuggestionRow from "./MealSuggestionRow.svelte";

function make_suggestion(overrides: Record<string, any> = {}) {
	return {
		foodName: "Apple",
		mealName: null,
		carbs: 25,
		matchScore: 0.85,
		...overrides,
	} as any;
}

describe("MealSuggestionRow", () => {
	it("renders food name and carb info", async () => {
		render(MealSuggestionRow, {
			suggestion: make_suggestion(),
			onAccept: () => {},
			onDismiss: () => {},
			onReview: () => {},
		});

		await expect.element(page.getByText("Apple")).toBeVisible();
		await expect.element(page.getByText(/25g carbs/)).toBeVisible();
		await expect.element(page.getByText(/85% match/)).toBeVisible();
	});

	it("falls back to mealName when foodName is null", async () => {
		render(MealSuggestionRow, {
			suggestion: make_suggestion({ foodName: null, mealName: "Lunch" }),
			onAccept: () => {},
			onDismiss: () => {},
			onReview: () => {},
		});

		await expect.element(page.getByText("Lunch")).toBeVisible();
	});

	it("falls back to 'Food entry' when both names are null", async () => {
		render(MealSuggestionRow, {
			suggestion: make_suggestion({ foodName: null, mealName: null }),
			onAccept: () => {},
			onDismiss: () => {},
			onReview: () => {},
		});

		await expect.element(page.getByText("Food entry")).toBeVisible();
	});

	it("renders Dismiss, Review, and Accept buttons", async () => {
		render(MealSuggestionRow, {
			suggestion: make_suggestion(),
			onAccept: () => {},
			onDismiss: () => {},
			onReview: () => {},
		});

		await expect
			.element(page.getByRole("button", { name: "Dismiss" }))
			.toBeVisible();
		await expect
			.element(page.getByRole("button", { name: "Review" }))
			.toBeVisible();
		await expect
			.element(page.getByRole("button", { name: "Accept" }))
			.toBeVisible();
	});

	it("calls onAccept when Accept is clicked", async () => {
		const on_accept = vi.fn();
		const suggestion = make_suggestion();

		render(MealSuggestionRow, {
			suggestion,
			onAccept: on_accept,
			onDismiss: () => {},
			onReview: () => {},
		});

		await page.getByRole("button", { name: "Accept" }).click();
		expect(on_accept).toHaveBeenCalledWith(suggestion);
	});

	it("calls onDismiss when Dismiss is clicked", async () => {
		const on_dismiss = vi.fn();
		const suggestion = make_suggestion();

		render(MealSuggestionRow, {
			suggestion,
			onAccept: () => {},
			onDismiss: on_dismiss,
			onReview: () => {},
		});

		await page.getByRole("button", { name: "Dismiss" }).click();
		expect(on_dismiss).toHaveBeenCalledWith(suggestion);
	});

	it("calls onReview when Review is clicked", async () => {
		const on_review = vi.fn();
		const suggestion = make_suggestion();

		render(MealSuggestionRow, {
			suggestion,
			onAccept: () => {},
			onDismiss: () => {},
			onReview: on_review,
		});

		await page.getByRole("button", { name: "Review" }).click();
		expect(on_review).toHaveBeenCalledWith(suggestion);
	});

	it("rounds match score to nearest integer", async () => {
		render(MealSuggestionRow, {
			suggestion: make_suggestion({ matchScore: 0.876 }),
			onAccept: () => {},
			onDismiss: () => {},
			onReview: () => {},
		});

		await expect.element(page.getByText(/88% match/)).toBeVisible();
	});

	it("handles zero match score", async () => {
		render(MealSuggestionRow, {
			suggestion: make_suggestion({ matchScore: 0 }),
			onAccept: () => {},
			onDismiss: () => {},
			onReview: () => {},
		});

		await expect.element(page.getByText(/0% match/)).toBeVisible();
	});

	it("handles null match score", async () => {
		render(MealSuggestionRow, {
			suggestion: make_suggestion({ matchScore: null }),
			onAccept: () => {},
			onDismiss: () => {},
			onReview: () => {},
		});

		await expect.element(page.getByText(/0% match/)).toBeVisible();
	});
});
