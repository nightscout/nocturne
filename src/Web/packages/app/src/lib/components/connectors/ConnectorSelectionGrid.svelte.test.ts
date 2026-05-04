import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import ConnectorSelectionGrid from "./ConnectorSelectionGrid.svelte";
import type {
	AvailableConnector,
	ServicesOverview,
} from "$lib/api/generated/nocturne-api-client";

function makeConnector(
	overrides: Partial<AvailableConnector> = {},
): AvailableConnector {
	return {
		id: "test-connector",
		name: "Test Connector",
		description: "A test data source",
		icon: undefined,
		available: true,
		isConfigured: false,
		...overrides,
	};
}

function makeOverview(
	connectors: AvailableConnector[],
): ServicesOverview {
	return { availableConnectors: connectors };
}

describe("ConnectorSelectionGrid", () => {
	it("shows loading skeleton when isLoading is true", async () => {
		render(ConnectorSelectionGrid, {
			servicesOverview: null,
			isLoading: true,
			error: null,
			onSelect: vi.fn(),
		});

		// Loading state should not show the title or empty state
		await expect
			.element(page.getByText("Choose a connector"))
			.not.toBeInTheDocument();
		await expect
			.element(page.getByText("No connectors available"))
			.not.toBeInTheDocument();
	});

	it("shows error message when error is non-null", async () => {
		render(ConnectorSelectionGrid, {
			servicesOverview: null,
			isLoading: false,
			error: "Failed to load connectors",
			onSelect: vi.fn(),
		});

		await expect
			.element(page.getByText("Error"))
			.toBeVisible();
		await expect
			.element(page.getByText("Failed to load connectors"))
			.toBeVisible();
	});

	it("renders connector names and descriptions in the grid", async () => {
		const connectors = [
			makeConnector({ id: "dexcom", name: "Dexcom", description: "Dexcom CGM data" }),
			makeConnector({ id: "libre", name: "LibreLink", description: "FreeStyle Libre data" }),
		];

		render(ConnectorSelectionGrid, {
			servicesOverview: makeOverview(connectors),
			isLoading: false,
			error: null,
			onSelect: vi.fn(),
		});

		await expect
			.element(page.getByText("Choose a connector"))
			.toBeVisible();
		await expect
			.element(page.getByText("Select a data source to configure"))
			.toBeVisible();
		await expect
			.element(page.getByText("Dexcom", { exact: true }))
			.toBeVisible();
		await expect
			.element(page.getByText("Dexcom CGM data"))
			.toBeVisible();
		await expect
			.element(page.getByText("LibreLink", { exact: true }))
			.toBeVisible();
		await expect
			.element(page.getByText("FreeStyle Libre data"))
			.toBeVisible();
	});

	it("shows Configured badge for configured connectors", async () => {
		const connectors = [
			makeConnector({ id: "dexcom", name: "Dexcom", isConfigured: true }),
			makeConnector({ id: "libre", name: "LibreLink", isConfigured: false }),
		];

		render(ConnectorSelectionGrid, {
			servicesOverview: makeOverview(connectors),
			isLoading: false,
			error: null,
			onSelect: vi.fn(),
		});

		await expect
			.element(page.getByText("Configured"))
			.toBeVisible();
	});

	it("does not show Configured badge for unconfigured connectors only", async () => {
		const connectors = [
			makeConnector({ id: "libre", name: "LibreLink", isConfigured: false }),
		];

		render(ConnectorSelectionGrid, {
			servicesOverview: makeOverview(connectors),
			isLoading: false,
			error: null,
			onSelect: vi.fn(),
		});

		await expect
			.element(page.getByText("Configured"))
			.not.toBeInTheDocument();
	});

	it("shows empty state when servicesOverview has no connectors", async () => {
		render(ConnectorSelectionGrid, {
			servicesOverview: { availableConnectors: undefined },
			isLoading: false,
			error: null,
			onSelect: vi.fn(),
		});

		await expect
			.element(page.getByText("No connectors available"))
			.toBeVisible();
		await expect
			.element(
				page.getByText(
					"No server-side connectors are registered in this installation.",
				),
			)
			.toBeVisible();
	});

	it("shows empty state when servicesOverview is null", async () => {
		render(ConnectorSelectionGrid, {
			servicesOverview: null,
			isLoading: false,
			error: null,
			onSelect: vi.fn(),
		});

		await expect
			.element(page.getByText("No connectors available"))
			.toBeVisible();
	});

	it("shows cancel button when onCancel is provided", async () => {
		render(ConnectorSelectionGrid, {
			servicesOverview: null,
			isLoading: false,
			error: null,
			onSelect: vi.fn(),
			onCancel: vi.fn(),
		});

		await expect
			.element(page.getByRole("button", { name: "Cancel" }))
			.toBeVisible();
	});

	it("does not show cancel button when onCancel is not provided", async () => {
		render(ConnectorSelectionGrid, {
			servicesOverview: null,
			isLoading: false,
			error: null,
			onSelect: vi.fn(),
		});

		await expect
			.element(page.getByRole("button", { name: "Cancel" }))
			.not.toBeInTheDocument();
	});

	it("calls onCancel when cancel button is clicked", async () => {
		const on_cancel = vi.fn();

		render(ConnectorSelectionGrid, {
			servicesOverview: null,
			isLoading: false,
			error: null,
			onSelect: vi.fn(),
			onCancel: on_cancel,
		});

		await page.getByRole("button", { name: "Cancel" }).click();
		expect(on_cancel).toHaveBeenCalledOnce();
	});

	it("calls onSelect with the correct connector when clicked", async () => {
		const dexcom = makeConnector({
			id: "dexcom",
			name: "Dexcom",
			description: "Dexcom CGM data",
		});
		const libre = makeConnector({
			id: "libre",
			name: "LibreLink",
			description: "FreeStyle Libre data",
		});
		const on_select = vi.fn();

		render(ConnectorSelectionGrid, {
			servicesOverview: makeOverview([dexcom, libre]),
			isLoading: false,
			error: null,
			onSelect: on_select,
		});

		await page.getByText("LibreLink").click();
		expect(on_select).toHaveBeenCalledOnce();
		expect(on_select).toHaveBeenCalledWith(libre);
	});

	it("calls onSelect with configured connector when clicked", async () => {
		const connector = makeConnector({
			id: "dexcom",
			name: "Dexcom",
			isConfigured: true,
		});
		const on_select = vi.fn();

		render(ConnectorSelectionGrid, {
			servicesOverview: makeOverview([connector]),
			isLoading: false,
			error: null,
			onSelect: on_select,
		});

		await page.getByText("Dexcom").click();
		expect(on_select).toHaveBeenCalledOnce();
		expect(on_select).toHaveBeenCalledWith(connector);
	});
});
