import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import SensorAge from "./SensorAge.svelte";

describe("SensorAge", () => {
	it("renders days format", () => {
		const threeDaysAgo = Date.now() - 3 * 24 * 60 * 60 * 1000;
		const { container } = render(SensorAge, {
			value: { startedAtMs: threeDaysAgo },
			options: { format: "days" },
		});
		const el = container.querySelector("[data-testid='sensor-age']");
		expect(el!.textContent).toContain("3d");
	});

	it("renders placeholder when value is null", () => {
		const { container } = render(SensorAge, {
			value: null,
			options: { format: "days" },
		});
		const el = container.querySelector("[data-testid='sensor-age']");
		expect(el!.textContent).toContain("--");
	});

	it("renders until-expiry format with remaining time", () => {
		const expiry = Date.now() + 4 * 60 * 60 * 1000; // 4h from now
		const { container } = render(SensorAge, {
			value: { startedAtMs: Date.now() - 1000, expiryMs: expiry },
			options: { format: "until-expiry" },
		});
		const el = container.querySelector("[data-testid='sensor-age']");
		expect(el!.textContent).toContain("left");
	});
});
