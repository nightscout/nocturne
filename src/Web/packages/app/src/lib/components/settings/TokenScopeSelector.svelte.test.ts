import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import TokenScopeSelector from "./TokenScopeSelector.svelte";

describe("TokenScopeSelector", () => {
  it("offers the shared category labels", async () => {
    render(TokenScopeSelector, { props: { selected: [] } });

    for (const label of ["Blood glucose", "Device status", "Treatment profile", "Sharing", "All health data", "All permissions"]) {
      await expect.element(page.getByText(label, { exact: true })).toBeVisible();
    }
  });

  it("marks a category covered when all health data is granted", async () => {
    render(TokenScopeSelector, { props: { selected: ["health.read", "health.readwrite"] } });

    await expect.element(page.getByText("Covered by Health").first()).toBeVisible();
  });
});
