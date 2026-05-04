import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { createRawSnippet } from "svelte";
import ResourceGuard from "./ResourceGuard.svelte";

// Helper to create a wrapper that provides children snippet
// ResourceGuard requires a children snippet, so we test it via a wrapper
// Since we can't easily pass snippets in vitest-browser-svelte, we test the
// key states by testing what's visible/hidden.

describe("ResourceGuard", () => {
	it("shows loading skeleton when loading without data", async () => {
		render(ResourceGuard, {
			loading: true,
			error: null,
			hasData: false,
			children: createRawSnippet(() => ({
				render: () => "<p>Content loaded</p>",
			})),
		});

		// Should not show content
		await expect
			.element(page.getByText("Content loaded"))
			.not.toBeInTheDocument();
	});

	it("shows error message in compact mode", async () => {
		render(ResourceGuard, {
			loading: false,
			error: "Something went wrong",
			compact: true,
			children: createRawSnippet(() => ({
				render: () => "<p>Content loaded</p>",
			})),
		});

		await expect
			.element(page.getByText("Something went wrong"))
			.toBeVisible();
	});

	it("shows error message in full-page mode", async () => {
		render(ResourceGuard, {
			loading: false,
			error: "Network error occurred",
			compact: false,
			children: createRawSnippet(() => ({
				render: () => "<p>Content loaded</p>",
			})),
		});

		await expect
			.element(page.getByText("Network error occurred"))
			.toBeVisible();
		await expect
			.element(page.getByText("Error Loading Data"))
			.toBeVisible();
	});

	it("shows custom error title", async () => {
		render(ResourceGuard, {
			loading: false,
			error: "Failed to load",
			errorTitle: "Report Error",
			compact: false,
			children: createRawSnippet(() => ({
				render: () => "<p>Content loaded</p>",
			})),
		});

		await expect
			.element(page.getByText("Report Error"))
			.toBeVisible();
	});

	it("shows retry button when onRetry is provided", async () => {
		const on_retry = vi.fn();

		render(ResourceGuard, {
			loading: false,
			error: "Failed",
			compact: true,
			onRetry: on_retry,
			children: createRawSnippet(() => ({
				render: () => "<p>Content loaded</p>",
			})),
		});

		await expect
			.element(page.getByRole("button", { name: /Try again/i }))
			.toBeVisible();
	});

	it("does not show retry button when onRetry is not provided", async () => {
		render(ResourceGuard, {
			loading: false,
			error: "Failed",
			compact: true,
			children: createRawSnippet(() => ({
				render: () => "<p>Content loaded</p>",
			})),
		});

		await expect
			.element(page.getByRole("button", { name: /Try again/i }))
			.not.toBeInTheDocument();
	});

	it("extracts message from Error objects", async () => {
		render(ResourceGuard, {
			loading: false,
			error: new Error("Custom error message"),
			compact: true,
			children: createRawSnippet(() => ({
				render: () => "<p>Content loaded</p>",
			})),
		});

		await expect
			.element(page.getByText("Custom error message"))
			.toBeVisible();
	});

	it("shows content when loaded successfully", async () => {
		render(ResourceGuard, {
			loading: false,
			error: null,
			children: createRawSnippet(() => ({
				render: () => "<p>Content loaded</p>",
			})),
		});

		await expect
			.element(page.getByText("Content loaded"))
			.toBeVisible();
	});

	it("prevents skeleton flash when hasData is true during loading", async () => {
		render(ResourceGuard, {
			loading: true,
			error: null,
			hasData: true,
			children: createRawSnippet(() => ({
				render: () => "<p>Cached content</p>",
			})),
		});

		// With hasData=true, should show content not skeleton
		await expect
			.element(page.getByText("Cached content"))
			.toBeVisible();
	});
});
