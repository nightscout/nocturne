import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import GlucoseScoreCard from "./GlucoseScoreCard.svelte";

describe("GlucoseScoreCard", () => {
	it("renders title and value", async () => {
		render(GlucoseScoreCard, {
			title: "Time in Range",
			value: 72,
			unit: "%",
			explanation: "Percentage of time glucose was in target range",
		});

		await expect.element(page.getByText("Time in Range")).toBeVisible();
		await expect.element(page.getByText("72")).toBeVisible();
		await expect.element(page.getByText("%")).toBeVisible();
	});

	it("renders explanation text", async () => {
		render(GlucoseScoreCard, {
			title: "GMI",
			value: "6.5",
			explanation: "Glucose management indicator, estimated A1C equivalent",
		});

		await expect
			.element(page.getByText("Glucose management indicator, estimated A1C equivalent"))
			.toBeVisible();
	});

	it("renders status badge with label", async () => {
		render(GlucoseScoreCard, {
			title: "TIR",
			value: 80,
			explanation: "Great time in range",
			status: "excellent",
		});

		await expect.element(page.getByText("Excellent")).toBeVisible();
	});

	it("renders 'Good' status by default", async () => {
		render(GlucoseScoreCard, {
			title: "TIR",
			value: 65,
			explanation: "Good TIR",
		});

		await expect.element(page.getByText("Good", { exact: true })).toBeVisible();
	});

	it("renders 'Needs Attention' status", async () => {
		render(GlucoseScoreCard, {
			title: "TIR",
			value: 40,
			explanation: "Low TIR",
			status: "needs-attention",
		});

		await expect.element(page.getByText("Needs Attention")).toBeVisible();
	});

	it("renders 'Critical' status", async () => {
		render(GlucoseScoreCard, {
			title: "TIR",
			value: 20,
			explanation: "Very low TIR",
			status: "critical",
		});

		await expect.element(page.getByText("Critical")).toBeVisible();
	});

	it("renders trend label when provided", async () => {
		render(GlucoseScoreCard, {
			title: "Avg Glucose",
			value: 145,
			unit: "mg/dL",
			explanation: "Average glucose over period",
			trend: "up",
			trendLabel: "+5 mg/dL",
		});

		await expect.element(page.getByText("+5 mg/dL")).toBeVisible();
	});

	it("renders target range with min and max", async () => {
		render(GlucoseScoreCard, {
			title: "TIR",
			value: 70,
			unit: "%",
			explanation: "Time in range",
			targetRange: { min: 70, max: 100 },
		});

		await expect.element(page.getByText("Target:")).toBeVisible();
		await expect.element(page.getByText(/70 – 100/)).toBeVisible();
	});

	it("renders target range with optimal value", async () => {
		render(GlucoseScoreCard, {
			title: "TIR",
			value: 70,
			unit: "%",
			explanation: "Time in range",
			targetRange: { optimal: 70 },
		});

		await expect.element(page.getByText(/≥ 70/)).toBeVisible();
	});

	it("renders clinical context when provided", async () => {
		render(GlucoseScoreCard, {
			title: "GMI",
			value: "6.5",
			explanation: "Estimated A1C",
			clinicalContext: "GMI correlates with A1C in clinical studies",
		});

		await expect.element(page.getByText("Clinical context")).toBeVisible();
	});

	it("does not render clinical context when not provided", async () => {
		render(GlucoseScoreCard, {
			title: "GMI",
			value: "6.5",
			explanation: "Estimated A1C",
		});

		await expect
			.element(page.getByText("Clinical context"))
			.not.toBeInTheDocument();
	});

	it("renders progress bar when showProgress is true", async () => {
		render(GlucoseScoreCard, {
			title: "TIR",
			value: 75,
			explanation: "Time in range",
			showProgress: true,
			progressValue: 75,
			progressMax: 100,
		});

		// The Progress component should render
		await expect.element(page.getByText("75")).toBeVisible();
	});
});
