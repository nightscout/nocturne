import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, afterEach } from "vitest";
import ZoomIndicator from "./ZoomIndicator.svelte";
import { timeFormat } from "$lib/stores/appearance-store.svelte";
import { time } from "$lib/utils/formatting";

describe("ZoomIndicator", () => {
	afterEach(() => {
		timeFormat.current = "12";
	});

	it("renders nothing when not zoomed", async () => {
		render(ZoomIndicator, {
			isZoomed: false,
			brushXDomain: null,
			onResetZoom: vi.fn(),
		});

		await expect
			.element(page.getByText("Zoomed view"))
			.not.toBeInTheDocument();
	});

	it("shows 'Zoomed view' text when zoomed", async () => {
		render(ZoomIndicator, {
			isZoomed: true,
			brushXDomain: null,
			onResetZoom: vi.fn(),
		});

		await expect
			.element(page.getByText("Zoomed view"))
			.toBeVisible();
	});

	it("shows time range when brushXDomain is provided", async () => {
		const start = new Date(2026, 3, 26, 8, 0);
		const end = new Date(2026, 3, 26, 12, 30);

		render(ZoomIndicator, {
			isZoomed: true,
			brushXDomain: [start, end],
			onResetZoom: vi.fn(),
		});

		await expect
			.element(page.getByText(`${time(start)} - ${time(end)}`))
			.toBeVisible();
	});

	// The chart used to format its own times, so the preference reached the
	// settings page and the mini overview strip but not the chart itself. Both
	// directions are asserted because whichever one matches the browser's own
	// locale would pass without the preference being consulted at all.
	it.each([
		{ format: "24" as const, shown: "20:30", hidden: "8:30" },
		{ format: "12" as const, shown: "8:30", hidden: "20:30" },
	])("honours the $format-hour preference", async ({ format, shown, hidden }) => {
		timeFormat.current = format;

		render(ZoomIndicator, {
			isZoomed: true,
			brushXDomain: [new Date(2026, 3, 26, 8, 0), new Date(2026, 3, 26, 20, 30)],
			onResetZoom: vi.fn(),
		});

		await expect.element(page.getByText(shown, { exact: false })).toBeVisible();
		await expect
			.element(page.getByText(hidden, { exact: true }))
			.not.toBeInTheDocument();
	});

	it("calls onResetZoom when reset button is clicked", async () => {
		const on_reset_zoom = vi.fn();

		render(ZoomIndicator, {
			isZoomed: true,
			brushXDomain: null,
			onResetZoom: on_reset_zoom,
		});

		await page.getByRole("button", { name: /Reset zoom/i }).click();

		expect(on_reset_zoom).toHaveBeenCalledOnce();
	});

	it("shows reset zoom button text when zoomed", async () => {
		render(ZoomIndicator, {
			isZoomed: true,
			brushXDomain: null,
			onResetZoom: vi.fn(),
		});

		await expect
			.element(page.getByRole("button", { name: /Reset zoom/i }))
			.toBeVisible();
	});
});
