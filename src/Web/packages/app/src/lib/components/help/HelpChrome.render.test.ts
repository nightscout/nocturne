import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import Harness from "./HelpChromeHarness.test.svelte";

describe("help before browser preferences resolve", () => {
  it("does not send a help bar or icon in the server HTML", () => {
    const { body } = render(Harness);
    expect(body).not.toContain('aria-label="Page help"');
    expect(body).not.toContain('aria-label="Open page help"');
  });
});
