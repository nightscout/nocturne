import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import OidcProviderDialog from "./OidcProviderDialog.svelte";
import type {
	OidcProviderResponse,
	TenantRoleDto,
} from "$lib/api/generated/nocturne-api-client";

function make_provider(
	overrides: Partial<OidcProviderResponse> = {},
): OidcProviderResponse {
	return {
		id: "provider-1",
		name: "Test Provider",
		issuerUrl: "https://accounts.example.com",
		clientId: "client-123",
		hasSecret: true,
		scopes: ["openid", "profile", "email"],
		defaultRoles: ["readable"],
		isEnabled: true,
		displayOrder: 5,
		icon: "google",
		buttonColor: "#4285f4",
		...overrides,
	};
}

const NO_ROLES: TenantRoleDto[] = [];

function default_props(overrides: Record<string, unknown> = {}) {
	return {
		open: false,
		editingProvider: null,
		roles: NO_ROLES,
		onSave: vi.fn(async () => {}),
		onCancel: vi.fn(),
		...overrides,
	};
}

describe("OidcProviderDialog", () => {
	it("does not render dialog content when open is false", async () => {
		render(OidcProviderDialog, default_props({ open: false }));

		await expect
			.element(page.getByText("Add identity provider"))
			.not.toBeInTheDocument();
	});

	it("renders Add Identity Provider title when open with no editingProvider", async () => {
		render(OidcProviderDialog, default_props({ open: true }));

		await expect
			.element(page.getByText("Add identity provider"))
			.toBeVisible();
		await expect
			.element(
				page.getByText(
					"Configure an external identity provider (OpenID Connect or OAuth 2.0) for single sign-on.",
				),
			)
			.toBeVisible();
	});

	it("shows all form fields", async () => {
		render(OidcProviderDialog, default_props({ open: true }));

		await expect.element(page.getByLabelText("Name")).toBeVisible();
		await expect.element(page.getByLabelText("Issuer URL")).toBeVisible();
		await expect.element(page.getByLabelText("Client ID")).toBeVisible();
		await expect.element(page.getByLabelText("Client secret")).toBeVisible();
		await expect.element(page.getByLabelText("Scopes")).toBeVisible();
		await expect.element(page.getByLabelText("Default roles")).toBeVisible();
		await expect.element(page.getByLabelText("Icon")).toBeVisible();
		await expect.element(page.getByLabelText("Button color")).toBeVisible();
		await expect.element(page.getByLabelText("Display order")).toBeVisible();
		await expect.element(page.getByLabelText("Enabled")).toBeVisible();
	});

	it("shows Create Provider button for new provider", async () => {
		render(OidcProviderDialog, default_props({ open: true }));

		await expect
			.element(page.getByRole("button", { name: "Create provider" }))
			.toBeVisible();
	});

	it("shows Edit Identity Provider title when editingProvider is set", async () => {
		const provider = make_provider();

		render(
			OidcProviderDialog,
			default_props({ open: true, editingProvider: provider }),
		);

		await expect
			.element(page.getByText("Edit identity provider"))
			.toBeVisible();
	});

	it("shows Save Changes button when editingProvider is set", async () => {
		const provider = make_provider();

		render(
			OidcProviderDialog,
			default_props({ open: true, editingProvider: provider }),
		);

		await expect
			.element(page.getByRole("button", { name: "Save changes" }))
			.toBeVisible();
	});

	it("pre-fills form fields from editingProvider data", async () => {
		const provider = make_provider({
			name: "Google",
			issuerUrl: "https://accounts.google.com",
			clientId: "google-client-id",
			scopes: ["openid", "profile"],
			defaultRoles: ["readable", "admin"],
			icon: "google",
			buttonColor: "#4285f4",
			displayOrder: 3,
			isEnabled: true,
		});

		render(
			OidcProviderDialog,
			default_props({ open: true, editingProvider: provider }),
		);

		await expect
			.element(page.getByLabelText("Name"))
			.toHaveValue("Google");
		await expect
			.element(page.getByLabelText("Issuer URL"))
			.toHaveValue("https://accounts.google.com");
		await expect
			.element(page.getByLabelText("Client ID"))
			.toHaveValue("google-client-id");
		await expect
			.element(page.getByLabelText("Client secret"))
			.toHaveValue("");
		await expect
			.element(page.getByLabelText("Scopes"))
			.toHaveValue("openid, profile");
		await expect
			.element(page.getByLabelText("Default roles"))
			.toHaveValue("readable, admin");
		await expect
			.element(page.getByLabelText("Icon"))
			.toHaveValue("google");
		await expect
			.element(page.getByLabelText("Button color"))
			.toHaveValue("#4285f4");
	});

	it("Test Connection button is disabled when Issuer URL and Client ID are empty", async () => {
		render(OidcProviderDialog, default_props({ open: true }));

		await expect
			.element(page.getByRole("button", { name: "Test connection" }))
			.toBeDisabled();
	});

	it("Cancel button is present when dialog is open", async () => {
		render(OidcProviderDialog, default_props({ open: true }));

		await expect
			.element(page.getByRole("button", { name: "Cancel" }))
			.toBeVisible();
	});

	it("shows default scopes value for new provider", async () => {
		render(OidcProviderDialog, default_props({ open: true }));

		await expect
			.element(page.getByLabelText("Scopes"))
			.toHaveValue("openid profile email");
	});

	it("shows default roles value for new provider", async () => {
		render(OidcProviderDialog, default_props({ open: true }));

		await expect
			.element(page.getByLabelText("Default roles"))
			.toHaveValue("readable");
	});
});
