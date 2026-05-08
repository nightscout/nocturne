import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import NowMarker from "./NowMarker.svelte";

describe("NowMarker", () => {
	it("renders halo and dot circles at the 12 o'clock position", () => {
		const { container } = render(NowMarker);

		const circles = container.querySelectorAll("circle");
		expect(circles).toHaveLength(2);

		const [halo, dot] = circles;

		expect(halo.getAttribute("cx")).toBe("70");
		expect(halo.getAttribute("cy")).toBe("15");
		expect(halo.getAttribute("r")).toBe("6");

		expect(dot.getAttribute("cx")).toBe("70");
		expect(dot.getAttribute("cy")).toBe("15");
		expect(dot.getAttribute("r")).toBe("2.5");
	});
});
