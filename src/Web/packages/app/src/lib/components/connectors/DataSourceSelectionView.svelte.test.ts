import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import type { UploaderApp } from "@nocturne/api-client";
import { UploaderCategory, UploaderPlatform } from "@nocturne/api-client";
import { getUploaderName } from "#lib/utils/uploader-labels.js";
import DataSourceSelectionView from "./DataSourceSelectionView.svelte";

const xdrip: UploaderApp = {
	id: "xdrip",
	platform: UploaderPlatform.Android,
	category: UploaderCategory.Cgm,
};

const props = {
	uploaderApps: [],
	dataSources: [],
	isLoading: false,
	loadError: null,
	onSelectConnector: vi.fn(),
	onSelectUploader: vi.fn(),
	onSkip: vi.fn(),
};

describe("DataSourceSelectionView", () => {
	it("does not offer Google Health, which is set up on its own page", async () => {
		render(DataSourceSelectionView, {
			...props,
			connectors: [
				{ id: "googlehealth", name: "Google Health", available: true },
				{ id: "dexcom", name: "Dexcom", available: true },
			],
		});

		await expect.element(page.getByText("Dexcom")).toBeInTheDocument();
		await expect
			.element(page.getByText("Google Health"))
			.not.toBeInTheDocument();
	});

	it("offers an interactive session the phone apps and their platform filter", async () => {
		render(DataSourceSelectionView, {
			...props,
			connectors: [{ id: "dexcom", name: "Dexcom", available: true }],
			uploaderApps: [xdrip],
		});

		await expect.element(page.getByRole("button", { name: getUploaderName(xdrip) })).toBeInTheDocument();
		await expect.element(page.getByRole("button", { name: "Android", exact: true })).toBeInTheDocument();
		expect(page.getByText("Sign in again to connect an app.").elements()).toHaveLength(0);
	});

	it("offers a handed-off session a sign-in in place of the phone apps, and keeps the cloud services", async () => {
		render(DataSourceSelectionView, {
			...props,
			connectors: [{ id: "dexcom", name: "Dexcom", available: true }],
			uploaderApps: [xdrip],
			signInRequiredForApps: true,
		});

		await expect.element(page.getByText("Dexcom")).toBeInTheDocument();
		await expect.element(page.getByText("Sign in again to connect an app.")).toBeVisible();
		expect(page.getByRole("button", { name: getUploaderName(xdrip) }).elements()).toHaveLength(0);
		expect(page.getByRole("button", { name: "Android", exact: true }).elements()).toHaveLength(0);
		expect(props.onSelectUploader).not.toHaveBeenCalled();
	});
});
