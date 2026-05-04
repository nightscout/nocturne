import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import ZoomIndicator from "./ZoomIndicator.svelte";

describe("ZoomIndicator", () => {
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

		const formatted_start = start.toLocaleTimeString([], {
			hour: "numeric",
			minute: "2-digit",
		});
		const formatted_end = end.toLocaleTimeString([], {
			hour: "numeric",
			minute: "2-digit",
		});

		await expect
			.element(page.getByText(`${formatted_start} - ${formatted_end}`))
			.toBeVisible();
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
