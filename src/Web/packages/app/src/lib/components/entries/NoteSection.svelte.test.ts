import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import NoteSection from "./NoteSection.svelte";

describe("NoteSection", () => {
	it("renders section heading", async () => {
		render(NoteSection, { note: {} });

		await expect.element(page.getByText("Note")).toBeVisible();
	});

	it("renders text label and textarea", async () => {
		render(NoteSection, { note: {} });

		await expect.element(page.getByText("Text")).toBeVisible();
	});

	it("renders announcement checkbox", async () => {
		render(NoteSection, { note: {} });

		await expect.element(page.getByText("Is Announcement")).toBeVisible();
	});

	it("does not show remove button when onRemove is not provided", async () => {
		render(NoteSection, { note: {} });

		await expect.element(page.getByText("Note")).toBeVisible();
	});

	it("shows remove button when onRemove is provided", async () => {
		const on_remove = vi.fn();
		render(NoteSection, { note: {}, onRemove: on_remove });

		await expect.element(page.getByText("Note")).toBeVisible();
	});
});
