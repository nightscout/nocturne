import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import CalendarHeader from "./CalendarHeader.svelte";

const MONTH_NAMES = [
	"January", "February", "March", "April", "May", "June",
	"July", "August", "September", "October", "November", "December",
];

describe("CalendarHeader", () => {
	it("renders the Calendar title", async () => {
		render(CalendarHeader, {
			viewDate: new Date(2025, 5, 15), // June 2025
			viewMode: "tir",
			isCurrentMonth: true,
			MONTH_NAMES,
			previousMonth: () => {},
			nextMonth: () => {},
			goToToday: () => {},
			setViewMode: () => {},
		});

		await expect.element(page.getByText("Calendar")).toBeVisible();
	});

	it("shows the current month and year", async () => {
		render(CalendarHeader, {
			viewDate: new Date(2025, 5, 15), // June
			viewMode: "tir",
			isCurrentMonth: true,
			MONTH_NAMES,
			previousMonth: () => {},
			nextMonth: () => {},
			goToToday: () => {},
			setViewMode: () => {},
		});

		await expect.element(page.getByText(/June/)).toBeVisible();
		await expect.element(page.getByText(/2025/)).toBeVisible();
	});

	it("hides Today button when viewing current month", async () => {
		render(CalendarHeader, {
			viewDate: new Date(),
			viewMode: "tir",
			isCurrentMonth: true,
			MONTH_NAMES,
			previousMonth: () => {},
			nextMonth: () => {},
			goToToday: () => {},
			setViewMode: () => {},
		});

		await expect
			.element(page.getByRole("button", { name: "Today" }))
			.not.toBeInTheDocument();
	});

	it("shows Today button when not viewing current month", async () => {
		render(CalendarHeader, {
			viewDate: new Date(2024, 0, 1),
			viewMode: "tir",
			isCurrentMonth: false,
			MONTH_NAMES,
			previousMonth: () => {},
			nextMonth: () => {},
			goToToday: () => {},
			setViewMode: () => {},
		});

		await expect
			.element(page.getByRole("button", { name: "Today" }))
			.toBeVisible();
	});

	it("calls previousMonth when left arrow clicked", async () => {
		const prev = vi.fn();

		render(CalendarHeader, {
			viewDate: new Date(2025, 5, 15),
			viewMode: "tir",
			isCurrentMonth: false,
			MONTH_NAMES,
			previousMonth: prev,
			nextMonth: () => {},
			goToToday: () => {},
			setViewMode: () => {},
		});

		// Click the first icon button (left arrow)
		const buttons = page.getByRole("button");
		await buttons.first().click();
		expect(prev).toHaveBeenCalled();
	});

	it("renders TIR and Profile toggle options", async () => {
		render(CalendarHeader, {
			viewDate: new Date(2025, 5, 15),
			viewMode: "tir",
			isCurrentMonth: true,
			MONTH_NAMES,
			previousMonth: () => {},
			nextMonth: () => {},
			goToToday: () => {},
			setViewMode: () => {},
		});

		await expect.element(page.getByText("TIR")).toBeVisible();
		await expect.element(page.getByText("Profile")).toBeVisible();
	});

	it("renders glucose color legend", async () => {
		render(CalendarHeader, {
			viewDate: new Date(2025, 5, 15),
			viewMode: "tir",
			isCurrentMonth: true,
			MONTH_NAMES,
			previousMonth: () => {},
			nextMonth: () => {},
			goToToday: () => {},
			setViewMode: () => {},
		});

		await expect.element(page.getByText("In Range")).toBeVisible();
		await expect.element(page.getByText("Low")).toBeVisible();
		await expect.element(page.getByText("High")).toBeVisible();
	});

	it("shows profile mode explanation when in profile view", async () => {
		render(CalendarHeader, {
			viewDate: new Date(2025, 5, 15),
			viewMode: "profile",
			isCurrentMonth: true,
			MONTH_NAMES,
			previousMonth: () => {},
			nextMonth: () => {},
			goToToday: () => {},
			setViewMode: () => {},
		});

		await expect.element(page.getByText("Daily glucose profile")).toBeVisible();
		await expect.element(page.getByText("Green band = target range")).toBeVisible();
	});

	it("shows click instruction", async () => {
		render(CalendarHeader, {
			viewDate: new Date(2025, 5, 15),
			viewMode: "tir",
			isCurrentMonth: true,
			MONTH_NAMES,
			previousMonth: () => {},
			nextMonth: () => {},
			goToToday: () => {},
			setViewMode: () => {},
		});

		await expect
			.element(page.getByText("Click any day to view detailed report"))
			.toBeVisible();
	});
});
