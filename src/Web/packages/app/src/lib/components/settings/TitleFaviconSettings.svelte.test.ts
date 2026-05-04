import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import TitleFaviconSettings from "./TitleFaviconSettings.svelte";

describe("TitleFaviconSettings", () => {
	it("renders the card title", async () => {
		render(TitleFaviconSettings);

		await expect.element(page.getByText("Browser Tab Settings")).toBeVisible();
		await expect
			.element(page.getByText("Customize how glucose data appears in the browser tab"))
			.toBeVisible();
	});

	it("renders the master enable toggle", async () => {
		render(TitleFaviconSettings);

		await expect
			.element(page.getByText("Enable dynamic title & favicon"))
			.toBeVisible();
		await expect
			.element(page.getByText("Show glucose values in browser tab"))
			.toBeVisible();
	});
});
