import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import PumpSiteAge from "./PumpSiteAge.svelte";

describe("PumpSiteAge", () => {
	it("renders days+hours format", () => {
		const ms = Date.now() - (2 * 24 * 60 * 60 + 5 * 60 * 60) * 1000;
		const { container } = render(PumpSiteAge, {
			value: { startedAtMs: ms },
			options: { format: "days+hours" },
		});
		const el = container.querySelector("[data-testid='pump-site-age']");
		expect(el!.textContent).toContain("2d");
		expect(el!.textContent).toContain("5h");
	});

	it("renders placeholder when value is null", () => {
		const { container } = render(PumpSiteAge, {
			value: null,
			options: { format: "days" },
		});
		const el = container.querySelector("[data-testid='pump-site-age']");
		expect(el!.textContent).toContain("--");
	});

	it("renders 'expired' when until-expiry is past", () => {
		const { container } = render(PumpSiteAge, {
			value: {
				startedAtMs: Date.now() - 10000,
				expiryMs: Date.now() - 1000,
			},
			options: { format: "until-expiry" },
		});
		const el = container.querySelector("[data-testid='pump-site-age']");
		expect(el!.textContent).toContain("expired");
	});
});
