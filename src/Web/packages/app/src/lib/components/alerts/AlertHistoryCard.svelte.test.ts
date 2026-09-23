import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import {
	AlertConditionType,
	type AlertHistoryResponse,
	type HistoryExcursionResponse,
} from "$api-clients";
import AlertHistoryCard from "./AlertHistoryCard.svelte";

function excursion(over: Partial<HistoryExcursionResponse> = {}): HistoryExcursionResponse {
	return {
		id: "1",
		ruleName: "Test",
		conditionType: AlertConditionType.Threshold,
		startedAt: new Date("2025-01-01T10:00:00Z"),
		endedAt: new Date("2025-01-01T10:30:00Z"),
		...over,
	};
}

function history(over: Partial<AlertHistoryResponse> = {}): AlertHistoryResponse {
	return {
		items: [excursion()],
		totalCount: 1,
		totalPages: 1,
		page: 1,
		...over,
	};
}

describe("AlertHistoryCard", () => {
	it("renders the card title and description", async () => {
		render(AlertHistoryCard, {
			history: null,
			page: 1,
			loading: false,
			onLoadPage: () => {},
		});

		await expect.element(page.getByText("Alert History", { exact: true })).toBeVisible();
		await expect
			.element(page.getByText("Past alert excursions and their resolution"))
			.toBeVisible();
	});

	it("shows empty state when no history", async () => {
		render(AlertHistoryCard, {
			history: null,
			page: 1,
			loading: false,
			onLoadPage: () => {},
		});

		await expect.element(page.getByText("No alert history")).toBeVisible();
		await expect
			.element(page.getByText("Resolved alerts will appear here"))
			.toBeVisible();
	});

	it("shows empty state with empty items array", async () => {
		render(AlertHistoryCard, {
			history: history({ items: [], totalCount: 0, totalPages: 0 }),
			page: 1,
			loading: false,
			onLoadPage: () => {},
		});

		await expect.element(page.getByText("No alert history")).toBeVisible();
	});

	it("renders history items in a table", async () => {
		render(AlertHistoryCard, {
			history: history({
				items: [
					excursion({
						ruleName: "Low Alert",
						acknowledgedAt: new Date("2025-01-01T10:05:00Z"),
						acknowledgedBy: "admin",
					}),
				],
			}),
			page: 1,
			loading: false,
			onLoadPage: () => {},
		});

		await expect.element(page.getByText("Low Alert")).toBeVisible();
		await expect.element(page.getByText("Threshold")).toBeVisible();
		await expect.element(page.getByText(/admin/)).toBeVisible();
	});

	it("renders table headers", async () => {
		render(AlertHistoryCard, {
			history: history(),
			page: 1,
			loading: false,
			onLoadPage: () => {},
		});

		await expect.element(page.getByText("Rule")).toBeVisible();
		await expect.element(page.getByText("Type")).toBeVisible();
		await expect.element(page.getByText("Started")).toBeVisible();
		await expect.element(page.getByText("Duration")).toBeVisible();
		await expect.element(page.getByText("Acknowledged")).toBeVisible();
	});

	it("does not show pagination for single page", async () => {
		render(AlertHistoryCard, {
			history: history(),
			page: 1,
			loading: false,
			onLoadPage: () => {},
		});

		await expect
			.element(page.getByRole("button", { name: /Previous/i }))
			.not.toBeInTheDocument();
		await expect
			.element(page.getByRole("button", { name: /Next/i }))
			.not.toBeInTheDocument();
	});

	it("shows pagination when multiple pages exist", async () => {
		render(AlertHistoryCard, {
			history: history({ totalCount: 25, totalPages: 3, page: 2 }),
			page: 2,
			loading: false,
			onLoadPage: () => {},
		});

		await expect
			.element(page.getByRole("button", { name: /Previous/i }))
			.toBeVisible();
		await expect
			.element(page.getByRole("button", { name: /Next/i }))
			.toBeVisible();
		await expect
			.element(page.getByText(/Page 2 of 3/))
			.toBeVisible();
	});

	it("disables Previous button on first page", async () => {
		render(AlertHistoryCard, {
			history: history({ totalCount: 20, totalPages: 2, page: 1 }),
			page: 1,
			loading: false,
			onLoadPage: () => {},
		});

		await expect
			.element(page.getByRole("button", { name: /Previous/i }))
			.toBeDisabled();
	});

	it("disables Next button on last page", async () => {
		render(AlertHistoryCard, {
			history: history({ totalCount: 20, totalPages: 2, page: 2 }),
			page: 2,
			loading: false,
			onLoadPage: () => {},
		});

		await expect
			.element(page.getByRole("button", { name: /Next/i }))
			.toBeDisabled();
	});
});
