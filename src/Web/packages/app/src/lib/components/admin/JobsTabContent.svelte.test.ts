import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import JobsTabContent from "./jobs-tab-content-test-wrapper.svelte";
import type { JobLedgerEntry } from "#lib/api/generated/nocturne-api-client.js";

const failing: JobLedgerEntry = {
	id: "row-failing",
	job: "alert-sweep",
	tenantId: "0193a1b2-0000-7000-8000-000000000001",
	nextDueAt: "2026-11-01T00:05:00Z",
	lastCompletedAt: "2026-10-31T23:59:30Z",
	attempts: 3,
	lastError: "NpgsqlException: connection refused",
};

const healthy: JobLedgerEntry = {
	id: "row-healthy",
	job: "dedup-reconciliation",
	tenantId: "0193a1b2-0000-7000-8000-000000000002",
	nextDueAt: "2026-11-01T00:02:00Z",
	lastCompletedAt: "2026-11-01T00:00:00Z",
	attempts: 0,
};

describe("JobsTabContent", () => {
	it("lists the failing row first and marks only that row", async () => {
		render(JobsTabContent, { jobs: [failing, healthy], loading: false, error: null });

		await expect.element(page.getByText("alert-sweep")).toBeVisible();
		const rows = page.getByRole("row").all().slice(1);
		expect(rows).toHaveLength(2);
		await expect.element(rows[0]).toHaveTextContent("alert-sweep");
		await expect.element(rows[0]).toHaveTextContent("NpgsqlException: connection refused");
		await expect.element(rows[0].getByLabelText("Failed")).toBeVisible();
		await expect.element(rows[1]).toHaveTextContent("dedup-reconciliation");
		await expect.element(rows[1].getByLabelText("Failed")).not.toBeInTheDocument();
	});

	it("says when the list is capped", async () => {
		render(JobsTabContent, { jobs: [failing, healthy], capped: true, loading: false, error: null });

		await expect.element(page.getByText("This list shows the first 2 rows only.")).toBeVisible();
	});

	it("shows a platform row without a tenant", async () => {
		render(JobsTabContent, {
			jobs: [{ ...healthy, id: "row-platform", job: "release-notice", tenantId: undefined }],
			loading: false,
			error: null,
		});

		await expect.element(page.getByText("Platform", { exact: true })).toBeVisible();
	});
});
