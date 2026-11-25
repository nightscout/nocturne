import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import ConnectorConfigForm from "./ConnectorConfigForm.svelte";

const testSchema = {
	type: "object",
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
				storedConfiguration: {},
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
			type: "object",
			properties: {
				syncGlucose: { type: "boolean", default: true },
			},
			secrets: [],
		};

		render(ConnectorConfigForm, {
			props: {
				storedConfiguration: {},
				schema: boolSchema,
				configuration: { syncGlucose: true },
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByText("Sync glucose")).toBeVisible();
		const toggle = page.getByRole("switch");
		await expect.element(toggle).toBeVisible();
	});

	it("disables a switch the API locks and says why", async () => {
		render(ConnectorConfigForm, {
			props: {
				storedConfiguration: {},
				schema: { type: "object", properties: { syncGlucose: { type: "boolean", default: false } }, secrets: [] },
				configuration: { syncGlucose: false },
				lockedKeys: ["syncGlucose"],
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByRole("switch")).toBeDisabled();
		await expect
			.element(page.getByTestId("locked-syncGlucose"))
			.toHaveTextContent("Only members who can manage this site's settings can turn this on.");
	});

	it("leaves a switch the API does not lock enabled, with no reason", async () => {
		render(ConnectorConfigForm, {
			props: {
				storedConfiguration: {},
				schema: { type: "object", properties: { syncGlucose: { type: "boolean", default: false } }, secrets: [] },
				configuration: { syncGlucose: false },
				lockedKeys: [],
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByRole("switch")).toBeEnabled();
		expect(page.getByTestId("locked-syncGlucose").elements()).toHaveLength(0);
	});

	it("renders number fields with constraints info", async () => {
		render(ConnectorConfigForm, {
			props: {
				storedConfiguration: {},
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
				storedConfiguration: {},
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
				storedConfiguration: {},
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
				storedConfiguration: {},
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
				storedConfiguration: {},
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
				storedConfiguration: {},
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

	it("marks every field type whose running value comes from the environment", async () => {
		const envSchema = {
			type: "object",
			properties: {
				syncGlucose: { type: "boolean", default: false },
				region: { type: "string", enum: ["EU", "US"], default: "EU" },
				pollingInterval: { type: "integer", default: 300 },
				serverUrl: { type: "string", default: "" },
			},
			secrets: [],
		};

		render(ConnectorConfigForm, {
			props: {
				storedConfiguration: {},
				schema: envSchema,
				configuration: {},
				effectiveConfig: {
					syncGlucose: true,
					region: "US",
					pollingInterval: 600,
					serverUrl: "https://env.example",
				},
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByText("From env").first()).toBeVisible();
		expect(page.getByText("From env").elements()).toHaveLength(4);
		await expect.element(page.getByRole("textbox").first()).toHaveValue("https://env.example");
	});

	it("does not mark a field whose environment value matches the default", async () => {
		render(ConnectorConfigForm, {
			props: {
				storedConfiguration: {},
				schema: { type: "object", properties: { serverUrl: { type: "string", default: "https://same.example" } }, secrets: [] },
				configuration: {},
				effectiveConfig: { serverUrl: "https://same.example" },
				onSave: vi.fn(),
			},
		});

		await expect.element(page.getByRole("textbox").first()).toHaveValue("https://same.example");
		expect(page.getByText("From env").elements()).toHaveLength(0);
	});

	it("offers a numeric choice by name and saves the number", async () => {
		const onSave = vi.fn().mockResolvedValue(undefined);
		render(ConnectorConfigForm, {
			props: {
				storedConfiguration: {},
				schema: {
					type: "object",
					properties: {
						syncIntervalMinutes: { type: "integer", enum: [1, 5, 60, 240], default: 5 },
					},
					secrets: [],
				},
				configuration: { syncIntervalMinutes: 5 },
				onSave,
			},
		});

		await expect.element(page.getByRole("button", { name: "5 minutes" })).toBeVisible();
		expect(page.getByRole("spinbutton").elements()).toHaveLength(0);

		await page.getByRole("button", { name: "5 minutes" }).click();
		await page.getByRole("option", { name: "4 hours" }).click();
		await expect.element(page.getByRole("button", { name: "4 hours" })).toBeVisible();

		await page.getByRole("button", { name: "Save" }).click();
		await vi.waitFor(() => expect(onSave).toHaveBeenCalled());
		expect(onSave.mock.calls[0][0]).toEqual({ syncIntervalMinutes: 240 });
	});

	it("names the save and makes it the one large action when a label is given", async () => {
		const onSave = vi.fn().mockResolvedValue(undefined);
		render(ConnectorConfigForm, {
			props: {
				storedConfiguration: {},
				schema: testSchema,
				configuration: { serverUrl: "https://example.com", pollingInterval: 300 },
				saveLabel: "Start import",
				onSave,
			},
		});

		await page.getByRole("textbox").first().fill("https://other.example");

		const start = page.getByRole("button", { name: "Start import" });
		await expect.element(start).toHaveClass("h-14");
		expect(page.getByRole("button", { name: "Save" }).elements()).toHaveLength(0);

		await start.click();
		await vi.waitFor(() => expect(onSave).toHaveBeenCalled());
	});

	describe("in setup", () => {
		const setupSchema = {
			type: "object",
			properties: {
				url: { type: "string", format: "uri", "x-address": true, "x-envVar": "CONNECT_NIGHTSCOUT_URL" },
				timezoneOffset: { type: "number", default: 0, "x-envVar": "CONNECT_NIGHTSCOUT_TIMEZONE_OFFSET" },
				glucoseProcessing: { type: "string", enum: ["Smoothed", "Unsmoothed"] },
				syncGlucose: { type: "boolean", default: true },
				maxCount: { type: "integer", default: 1000 },
				apiSecret: { type: "string", "x-secret": true },
			},
			secrets: ["apiSecret"],
		};

		it("shows only what connecting needs, with no environment names", async () => {
			render(ConnectorConfigForm, {
				props: {
					storedConfiguration: {},
					schema: setupSchema,
					configuration: { url: "https://ns.example" },
					effectiveConfig: { timezoneOffset: 2 },
					variant: "setup",
					onSave: vi.fn(),
				},
			});

			await expect.element(page.getByText("URL", { exact: true })).toBeVisible();
			await expect.element(page.getByText("API secret")).toBeVisible();
			await expect.element(page.getByText("(optional)")).toBeVisible();
			await expect.element(page.getByText("Timezone offset")).not.toBeInTheDocument();
			await expect.element(page.getByText("Glucose processing")).not.toBeInTheDocument();
			await expect.element(page.getByText("Sync glucose")).not.toBeInTheDocument();
			await expect.element(page.getByText(/CONNECT_NIGHTSCOUT/)).not.toBeInTheDocument();
			expect(page.getByTestId("connector-credentials").elements()).toHaveLength(0);
		});

		it("does not call a required secret optional", async () => {
			render(ConnectorConfigForm, {
				props: {
					storedConfiguration: {},
					schema: { ...setupSchema, required: ["apiSecret"] },
					configuration: {},
					variant: "setup",
					onSave: vi.fn(),
				},
			});

			await expect.element(page.getByText("API secret")).toBeVisible();
			await expect.element(page.getByText("(optional)")).not.toBeInTheDocument();
		});

		it("shows a required field of any category", async () => {
			render(ConnectorConfigForm, {
				props: {
					storedConfiguration: {},
					schema: { ...setupSchema, required: ["maxCount"] },
					configuration: {},
					variant: "setup",
					onSave: vi.fn(),
				},
			});

			await expect.element(page.getByText("Max count")).toBeVisible();
		});

		it("offers the save before anything changes", async () => {
			const onSave = vi.fn().mockResolvedValue(undefined);
			render(ConnectorConfigForm, {
				props: {
					storedConfiguration: {},
					schema: setupSchema,
					configuration: { url: "https://ns.example" },
					variant: "setup",
					saveLabel: "Start import",
					onSave,
				},
			});

			await page.getByRole("button", { name: "Start import" }).click();
			await vi.waitFor(() => expect(onSave).toHaveBeenCalled());
		});
	});
});
