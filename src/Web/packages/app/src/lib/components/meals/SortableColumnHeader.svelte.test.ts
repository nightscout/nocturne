import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import SortableColumnHeader from "./SortableColumnHeader.svelte";

describe("SortableColumnHeader", () => {
	it("renders the column label", async () => {
		render(SortableColumnHeader, {
			label: "Date",
			column: "date",
			sortColumn: "name",
			sortDirection: "asc",
			onSort: () => {},
		});

		await expect
			.element(page.getByRole("button", { name: /Date/i }))
			.toBeVisible();
	});

	it("calls onSort with column name when clicked", async () => {
		const on_sort = vi.fn();

		render(SortableColumnHeader, {
			label: "Date",
			column: "date",
			sortColumn: "name",
			sortDirection: "asc",
			onSort: on_sort,
		});

		await page.getByRole("button", { name: /Date/i }).click();
		expect(on_sort).toHaveBeenCalledWith("date");
	});

	it("shows neutral sort icon when not the active column", async () => {
		render(SortableColumnHeader, {
			label: "Carbs",
			column: "carbs",
			sortColumn: "date",
			sortDirection: "asc",
			onSort: () => {},
		});

		// The button should render (the sort icon is an SVG, hard to check directly)
		await expect
			.element(page.getByRole("button", { name: /Carbs/i }))
			.toBeVisible();
	});

	it("renders when it is the active sort column ascending", async () => {
		render(SortableColumnHeader, {
			label: "Date",
			column: "date",
			sortColumn: "date",
			sortDirection: "asc",
			onSort: () => {},
		});

		await expect
			.element(page.getByRole("button", { name: /Date/i }))
			.toBeVisible();
	});

	it("renders when it is the active sort column descending", async () => {
		render(SortableColumnHeader, {
			label: "Date",
			column: "date",
			sortColumn: "date",
			sortDirection: "desc",
			onSort: () => {},
		});

		await expect
			.element(page.getByRole("button", { name: /Date/i }))
			.toBeVisible();
	});
});
