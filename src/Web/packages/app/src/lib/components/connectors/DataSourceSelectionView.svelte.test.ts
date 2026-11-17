import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import DataSourceSelectionView from "./DataSourceSelectionView.svelte";

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
});
