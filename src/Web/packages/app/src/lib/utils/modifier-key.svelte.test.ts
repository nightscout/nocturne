import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, expect, it } from "vitest";
import Harness from "./ModifierKeyHarness.test.svelte";

function reportPlatform(platform: string) {
  Object.defineProperty(navigator, "userAgentData", { value: { platform }, configurable: true });
  Object.defineProperty(navigator, "platform", { value: platform, configurable: true });
}

afterEach(() => {
  delete (navigator as { userAgentData?: unknown }).userAgentData;
  delete (navigator as { platform?: unknown }).platform;
});

describe("useModifierKey", () => {
  it("labels the modifier Ctrl off a Mac", async () => {
    reportPlatform("Windows");
    render(Harness);

    await expect.element(page.getByTestId("label")).toHaveTextContent("Ctrl");
    await expect.element(page.getByTestId("is-mac")).toHaveTextContent("false");
  });

  it("labels the modifier with the command key on a Mac", async () => {
    reportPlatform("macOS");
    render(Harness);

    await expect.element(page.getByTestId("label")).toHaveTextContent("⌘");
    await expect.element(page.getByTestId("is-mac")).toHaveTextContent("true");
  });
});
