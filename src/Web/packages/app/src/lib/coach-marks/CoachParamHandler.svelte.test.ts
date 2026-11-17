import { render } from "vitest-browser-svelte";
import { beforeEach, describe, expect, it, vi } from "vitest";

const state = vi.hoisted(() => ({
  page: { url: new URL("http://sam.localhost/reports") },
  replaceState: vi.fn(),
  startSequence: vi.fn(),
}));

vi.mock("$app/state", () => ({ page: state.page }));
vi.mock("$app/navigation", () => ({
  replaceState: state.replaceState,
  beforeNavigate: () => {},
}));
vi.mock("@nocturne/coach", () => ({
  getCoachMarkContext: () => ({ clearQuiet: () => {}, startSequence: state.startSequence }),
}));

import CoachParamHandler from "./CoachParamHandler.svelte";

beforeEach(() => {
  state.replaceState.mockReset();
  state.startSequence.mockReset();
});

describe("CoachParamHandler", () => {
  it("strips the coach param from the URL and starts its sequence", async () => {
    state.page.url = new URL("http://sam.localhost/reports?range=7d&coach=agp-tour");

    render(CoachParamHandler);

    expect(String(state.replaceState.mock.calls[0][0])).toBe("http://sam.localhost/reports?range=7d");
    await vi.waitFor(() => expect(state.startSequence).toHaveBeenCalledWith("agp-tour"));
  });

  it("does nothing for a URL without the param", async () => {
    state.page.url = new URL("http://sam.localhost/reports?range=7d");

    render(CoachParamHandler);
    await new Promise((r) => setTimeout(r, 150));

    expect(state.replaceState).not.toHaveBeenCalled();
    expect(state.startSequence).not.toHaveBeenCalled();
  });
});
