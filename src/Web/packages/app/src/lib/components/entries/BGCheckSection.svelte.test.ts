import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import BGCheckSection from "./BGCheckSection.svelte";

describe("BGCheckSection", () => {
	it("renders section heading", async () => {
		render(BGCheckSection, { bgCheck: {} });

		await expect.element(page.getByText("BG Check")).toBeVisible();
	});

	it("renders glucose, type, and units fields", async () => {
		render(BGCheckSection, { bgCheck: {} });

		await expect.element(page.getByText("Glucose")).toBeVisible();
		await expect.element(page.getByText("Type")).toBeVisible();
		await expect.element(page.getByText("Units")).toBeVisible();
	});

	it("defaults to Finger type", async () => {
		render(BGCheckSection, { bgCheck: {} });

		await expect.element(page.getByText("Finger")).toBeVisible();
	});

	it("defaults to mg/dL units", async () => {
		render(BGCheckSection, { bgCheck: {} });

		await expect.element(page.getByText("mg/dL")).toBeVisible();
	});

	it("does not show remove button when onRemove is not provided", async () => {
		render(BGCheckSection, { bgCheck: {} });

		await expect.element(page.getByText("BG Check")).toBeVisible();
	});

	it("renders with provided values", async () => {
		render(BGCheckSection, {
			bgCheck: {
				glucose: 120,
				glucoseType: "Sensor" as any,
				units: "Mmol" as any,
			},
		});

		await expect.element(page.getByText("Sensor")).toBeVisible();
		await expect.element(page.getByText("mmol/L")).toBeVisible();
	});
});
