import { describe, it, expect } from "vitest";
import { sequences } from "./sequences";

describe("Coach mark sequences", () => {
	it("defines the onboarding sequence", () => {
		expect(sequences.onboarding).toBeDefined();
		expect(sequences.onboarding.priority).toBe(100);
		expect(sequences.onboarding.steps.length).toBeGreaterThan(0);
	});

	it("onboarding steps cover key setup areas", () => {
		const steps = sequences.onboarding.steps;
		expect(steps).toContain("onboarding.patient-details");
		expect(steps).toContain("onboarding.devices");
		expect(steps).toContain("onboarding.alerts");
		expect(steps).toContain("onboarding.sharing");
	});

	it("dashboard-discovery requires onboarding as prerequisite", () => {
		expect(sequences["dashboard-discovery"].prerequisite).toBe("onboarding");
	});

	it("feature-intro requires onboarding as prerequisite", () => {
		expect(sequences["feature-intro"].prerequisite).toBe("onboarding");
	});

	it("power-user requires onboarding as prerequisite", () => {
		expect(sequences["power-user"].prerequisite).toBe("onboarding");
	});

	it("quick-tour has highest priority", () => {
		expect(sequences["quick-tour"].priority).toBe(200);
	});

	it("quick-tour has no prerequisite", () => {
		expect(sequences["quick-tour"]).not.toHaveProperty("prerequisite");
	});

	it("setup-alerts completes onboarding.alerts key", () => {
		expect(sequences["setup-alerts"].completesKeys).toContain("onboarding.alerts");
	});

	it("setup-reports completes dashboard-discovery.reports key", () => {
		expect(sequences["setup-reports"].completesKeys).toContain("dashboard-discovery.reports");
	});

	it("setup-connectors completes power-user.connectors key", () => {
		expect(sequences["setup-connectors"].completesKeys).toContain("power-user.connectors");
	});

	it("setup-invite completes onboarding.sharing key", () => {
		expect(sequences["setup-invite"].completesKeys).toContain("onboarding.sharing");
	});

	it("all sequences have positive priority", () => {
		for (const [name, seq] of Object.entries(sequences)) {
			expect(seq.priority, `${name} should have positive priority`).toBeGreaterThan(0);
		}
	});

	it("all sequences have at least one step", () => {
		for (const [name, seq] of Object.entries(sequences)) {
			expect(seq.steps.length, `${name} should have at least one step`).toBeGreaterThan(0);
		}
	});

	it("step keys follow dotted naming convention", () => {
		for (const [name, seq] of Object.entries(sequences)) {
			for (const step of seq.steps) {
				expect(step, `step in ${name} should be dotted`).toMatch(/^[\w-]+\.[\w-]+$/);
			}
		}
	});

	it("priority ordering is logical (onboarding > dashboard > feature > power-user)", () => {
		expect(sequences.onboarding.priority).toBeGreaterThan(
			sequences["dashboard-discovery"].priority,
		);
		expect(sequences["dashboard-discovery"].priority).toBeGreaterThan(
			sequences["feature-intro"].priority,
		);
		expect(sequences["feature-intro"].priority).toBeGreaterThan(
			sequences["power-user"].priority,
		);
	});
});
