import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import OidcProvidersTabContent from "./oidc-providers-tab-content-test-wrapper.svelte";
import type { OidcProviderResponse } from "$lib/api/generated/nocturne-api-client";

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
		displayOrder: 0,
		icon: undefined,
		buttonColor: undefined,
		...overrides,
	};
}

function default_props(overrides: Record<string, unknown> = {}) {
	return {
		providers: [] as OidcProviderResponse[],
		configManaged: false,
		loading: false,
		error: null,
		onAdd: vi.fn(),
		onEdit: vi.fn(),
		onDelete: vi.fn(),
		onToggle: vi.fn(),
		...overrides,
	};
}

describe("OidcProvidersTabContent", () => {
	it("renders nothing when configManaged is true", async () => {
		render(OidcProvidersTabContent, default_props({ configManaged: true }));

		await expect
			.element(page.getByText("Identity Providers"))
			.not.toBeInTheDocument();
		await expect
			.element(page.getByText("Add Provider"))
			.not.toBeInTheDocument();
	});

	it("renders title and description when configManaged is false", async () => {
		render(OidcProvidersTabContent, default_props());

		await expect
			.element(page.getByText("Identity Providers", { exact: true }))
			.toBeVisible();
		await expect
			.element(
				page.getByText(
					"Configure OpenID Connect providers for single sign-on.",
				),
			)
			.toBeVisible();
	});

	it("renders Add Provider button", async () => {
		render(OidcProvidersTabContent, default_props());

		await expect
			.element(page.getByRole("button", { name: /Add Provider/i }))
			.toBeVisible();
	});

	it("calls onAdd when Add Provider button is clicked", async () => {
		const on_add = vi.fn();
		render(OidcProvidersTabContent, default_props({ onAdd: on_add }));

		await page.getByRole("button", { name: /Add Provider/i }).click();
		expect(on_add).toHaveBeenCalledOnce();
	});

	it("shows empty state when providers list is empty", async () => {
		render(OidcProvidersTabContent, default_props({ providers: [] }));

		await expect
			.element(page.getByText("No identity providers configured."))
			.toBeVisible();
	});

	it("shows error alert when error is set", async () => {
		render(
			OidcProvidersTabContent,
			default_props({ error: "Failed to load providers" }),
		);

		await expect
			.element(page.getByText("Failed to load providers"))
			.toBeVisible();
	});

	it("renders provider name and issuer URL", async () => {
		const providers = [
			make_provider({
				name: "Google",
				issuerUrl: "https://accounts.google.com",
			}),
		];

		render(OidcProvidersTabContent, default_props({ providers }));

		await expect.element(page.getByText("Google", { exact: true })).toBeVisible();
		await expect
			.element(page.getByText("https://accounts.google.com"))
			.toBeVisible();
	});

	it("shows Enabled badge for enabled providers", async () => {
		const providers = [make_provider({ isEnabled: true })];

		render(OidcProvidersTabContent, default_props({ providers }));

		await expect.element(page.getByText("Enabled")).toBeVisible();
	});

	it("shows Disabled badge for disabled providers", async () => {
		const providers = [make_provider({ isEnabled: false })];

		render(OidcProvidersTabContent, default_props({ providers }));

		await expect.element(page.getByText("Disabled")).toBeVisible();
	});

	it("renders action buttons for each provider", async () => {
		const providers = [make_provider()];

		render(OidcProvidersTabContent, default_props({ providers }));

		await expect
			.element(page.getByRole("button", { name: "Edit" }))
			.toBeVisible();
		await expect
			.element(page.getByRole("button", { name: "Delete" }))
			.toBeVisible();
		await expect
			.element(page.getByRole("button", { name: "Disable" }))
			.toBeVisible();
	});

	it("shows Enable button for disabled providers", async () => {
		const providers = [make_provider({ isEnabled: false })];

		render(OidcProvidersTabContent, default_props({ providers }));

		await expect
			.element(page.getByRole("button", { name: "Enable" }))
			.toBeVisible();
	});

	it("calls onEdit when edit button is clicked", async () => {
		const provider = make_provider({ name: "Google" });
		const on_edit = vi.fn();

		render(
			OidcProvidersTabContent,
			default_props({ providers: [provider], onEdit: on_edit }),
		);

		await page.getByRole("button", { name: "Edit" }).click();
		expect(on_edit).toHaveBeenCalledOnce();
		expect(on_edit).toHaveBeenCalledWith(provider);
	});

	it("calls onDelete when delete button is clicked", async () => {
		const provider = make_provider({ name: "Google" });
		const on_delete = vi.fn();

		render(
			OidcProvidersTabContent,
			default_props({ providers: [provider], onDelete: on_delete }),
		);

		await page.getByRole("button", { name: "Delete" }).click();
		expect(on_delete).toHaveBeenCalledOnce();
		expect(on_delete).toHaveBeenCalledWith(provider);
	});

	it("calls onToggle when toggle button is clicked", async () => {
		const provider = make_provider({ isEnabled: true });
		const on_toggle = vi.fn();

		render(
			OidcProvidersTabContent,
			default_props({ providers: [provider], onToggle: on_toggle }),
		);

		await page.getByRole("button", { name: "Disable" }).click();
		expect(on_toggle).toHaveBeenCalledOnce();
		expect(on_toggle).toHaveBeenCalledWith(provider);
	});

	it("renders multiple providers", async () => {
		const providers = [
			make_provider({
				id: "p1",
				name: "Google",
				issuerUrl: "https://accounts.google.com",
			}),
			make_provider({
				id: "p2",
				name: "GitHub",
				issuerUrl: "https://github.com",
				isEnabled: false,
			}),
		];

		render(OidcProvidersTabContent, default_props({ providers }));

		await expect.element(page.getByText("Google", { exact: true })).toBeVisible();
		await expect.element(page.getByText("GitHub", { exact: true })).toBeVisible();
		await expect
			.element(page.getByText("https://accounts.google.com"))
			.toBeVisible();
		await expect
			.element(page.getByText("https://github.com"))
			.toBeVisible();
	});
});
