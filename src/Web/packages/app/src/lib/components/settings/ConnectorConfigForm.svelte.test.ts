import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import ConnectorConfigForm from "./ConnectorConfigForm.svelte";

const testSchema = {
	properties: {
		serverUrl: { type: "string", default: "" },
		pollingInterval: {
			type: "integer",
			default: 300,
			minimum: 60,
			maximum: 3600,
		},
		enabled: { type: "boolean", default: true },
	},
	secrets: ["apiKey"],
};

// Add apiKey to properties so the secret field renders
const schemaWithSecretProp = {
	...testSchema,
	properties: {
		...testSchema.properties,
		apiKey: { type: "string", default: "" },
	},
};

describe("ConnectorConfigForm", () => {
	it("renders string fields with labels", async () => {
		render(ConnectorConfigForm, {
			props: {
				schema: schemaWithSecretProp,
				configuration: { serverUrl: "https://example.com", pollingInterval: 300 },
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByText("Server Url")).toBeVisible();
		const input = page.getByRole("textbox").first();
		await expect.element(input).toHaveValue("https://example.com");
	});

	it("renders boolean fields as switches", async () => {
		// Use a schema where the boolean is NOT named "enabled" since that is filtered out
		const boolSchema = {
			properties: {
				syncGlucose: { type: "boolean", default: true },
			},
			secrets: [],
		};

		render(ConnectorConfigForm, {
			props: {
				schema: boolSchema,
				configuration: { syncGlucose: true },
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByText("Sync Glucose")).toBeVisible();
		const toggle = page.getByRole("switch");
		await expect.element(toggle).toBeVisible();
	});

	it("renders number fields with constraints info", async () => {
		render(ConnectorConfigForm, {
			props: {
				schema: schemaWithSecretProp,
				configuration: { serverUrl: "", pollingInterval: 300 },
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByText("Polling Interval")).toBeVisible();
		await expect
			.element(page.getByText("Value must be between 60 and 3600"))
			.toBeVisible();

		const numberInput = page.getByRole("spinbutton");
		await expect.element(numberInput).toHaveValue(300);
	});

	it("shows Credentials card when schema has secrets", async () => {
		render(ConnectorConfigForm, {
			props: {
				schema: schemaWithSecretProp,
				configuration: { serverUrl: "", pollingInterval: 300 },
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByText("Credentials", { exact: true })).toBeVisible();
		await expect.element(page.getByText("Api Key")).toBeVisible();
	});

	it("shows Configured badge when hasSecrets is true", async () => {
		render(ConnectorConfigForm, {
			props: {
				schema: schemaWithSecretProp,
				configuration: { serverUrl: "", pollingInterval: 300 },
				hasSecrets: true,
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByText("Configured")).toBeVisible();
	});

	it("shows Not configured badge when hasSecrets is false", async () => {
		render(ConnectorConfigForm, {
			props: {
				schema: schemaWithSecretProp,
				configuration: { serverUrl: "", pollingInterval: 300 },
				hasSecrets: false,
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByText("Not configured")).toBeVisible();
	});

	it("does not show save bar initially when there are no unsaved changes", async () => {
		render(ConnectorConfigForm, {
			props: {
				schema: schemaWithSecretProp,
				configuration: { serverUrl: "", pollingInterval: 300 },
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByText("You have unsaved changes")).not.toBeInTheDocument();
	});

	it("has show/hide toggle button for secret fields", async () => {
		render(ConnectorConfigForm, {
			props: {
				schema: schemaWithSecretProp,
				configuration: { serverUrl: "", pollingInterval: 300 },
				onSave: vi.fn(),
			},
		});

		// The secret field should have a password input and a toggle button next to it
		const secretInput = page.getByPlaceholder("Enter to update (leave blank to keep current)");
		await expect.element(secretInput).toBeVisible();
		await expect.element(secretInput).toHaveAttribute("type", "password");

		// The toggle button is an outline button with Eye icon next to the input
		const toggleButton = page.getByRole("button").first();
		await expect.element(toggleButton).toBeVisible();
	});
});
