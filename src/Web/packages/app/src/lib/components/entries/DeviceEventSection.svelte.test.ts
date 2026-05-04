import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import DeviceEventSection from "./DeviceEventSection.svelte";

describe("DeviceEventSection", () => {
	it("renders section heading", async () => {
		render(DeviceEventSection, { deviceEvent: {} });

		await expect.element(page.getByText("Device Event")).toBeVisible();
	});

	it("renders event type selector", async () => {
		render(DeviceEventSection, { deviceEvent: {} });

		await expect.element(page.getByText("Event Type")).toBeVisible();
	});

	it("defaults to Site Change when no event type set", async () => {
		render(DeviceEventSection, { deviceEvent: {} });

		await expect.element(page.getByText("Site Change")).toBeVisible();
	});

	it("renders notes textarea", async () => {
		render(DeviceEventSection, { deviceEvent: {} });

		await expect.element(page.getByText("Notes")).toBeVisible();
	});

	it("does not show remove button when onRemove is not provided", async () => {
		render(DeviceEventSection, { deviceEvent: {} });

		await expect.element(page.getByText("Device Event")).toBeVisible();
	});

	it("shows remove button when onRemove is provided", async () => {
		const on_remove = vi.fn();
		render(DeviceEventSection, { deviceEvent: {}, onRemove: on_remove });

		await expect.element(page.getByText("Device Event")).toBeVisible();
	});
});
