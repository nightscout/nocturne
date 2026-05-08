import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import {
	HaloDialColorMode,
	PumpModeState,
} from "$lib/api/generated/nocturne-api-client";
import PredictionRing from "./PredictionRing.svelte";

describe("PredictionRing", () => {
	it("applies suspended dasharray when pumpMode is Suspended", () => {
		const { container } = render(PredictionRing, {
			currentBg: 110,
			predictionValues: [120, 130, 140],
			predictionMinutes: 45,
			colorMode: HaloDialColorMode.Discrete,
			pumpMode: PumpModeState.Suspended,
		});

		const path = container.querySelector(
			"[data-testid='prediction-ring']",
		);
		expect(path).not.toBeNull();
		expect(path!.getAttribute("stroke-dasharray")).toBe("8 4");
	});

	it("renders without a stroke-dasharray attribute when pumpMode is null", () => {
		const { container } = render(PredictionRing, {
			currentBg: 110,
			predictionValues: [120, 130, 140],
			predictionMinutes: 45,
			colorMode: HaloDialColorMode.Discrete,
			pumpMode: null,
		});

		const path = container.querySelector(
			"[data-testid='prediction-ring']",
		);
		expect(path).not.toBeNull();
		expect(path!.hasAttribute("stroke-dasharray")).toBe(false);
	});

	it("renders the ghosted backdrop when predictionValues is empty", () => {
		const { container } = render(PredictionRing, {
			currentBg: 110,
			predictionValues: [],
			predictionMinutes: 45,
			colorMode: HaloDialColorMode.Discrete,
			pumpMode: null,
		});

		const main = container.querySelector(
			"[data-testid='prediction-ring']",
		);
		expect(main).toBeNull();

		const backdrop = container.querySelector(
			"[data-testid='prediction-ring-backdrop']",
		);
		expect(backdrop).not.toBeNull();
		expect(backdrop!.getAttribute("stroke-opacity")).toBe("0.08");
	});
});
