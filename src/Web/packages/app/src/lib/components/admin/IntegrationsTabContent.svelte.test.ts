import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import IntegrationsTabContent from "./integrations-tab-content-test-wrapper.svelte";
import type { PlatformSettingsSummary } from "$api";

const callback = "https://nocturne.example/api/v4/google-health/oauth/callback";
const platform: PlatformSettingsSummary = {
  category: "google-health",
  enabled: true,
  configuredFields: ["clientId", "clientSecret", "redirectUri"],
  fields: [
    { name: "clientId", label: "Client ID", required: true },
    { name: "clientSecret", label: "Client secret", required: true },
    {
      name: "redirectUri",
      label: "Callback URL",
      required: true,
      defaultValue: callback,
    },
  ],
};

describe("Google Health instance settings", () => {
  it("shows the exact public callback and a masked secret within the integrations card", async () => {
    render(IntegrationsTabContent, {
      platforms: [platform],
      onSave: vi.fn(),
      onDelete: vi.fn(),
    });
    await expect
      .element(page.getByRole("textbox", { name: "Callback URL" }))
      .toHaveValue(callback);
    await expect
      .element(page.getByLabelText("Client secret"))
      .toHaveAttribute("type", "password");
    await expect
      .element(page.getByText("Google publishing and verification"))
      .toBeVisible();
    await expect
      .element(page.getByRole("button", { name: "Save", exact: true }))
      .toBeVisible();
  });

  it("saves through the admin callback, clears the typed secret and preserves the callback URL", async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(IntegrationsTabContent, {
      platforms: [platform],
      onSave,
      onDelete: vi.fn(),
    });
    await page.getByLabelText("Client secret").fill("replacement-secret");
    await page.getByRole("button", { name: "Save", exact: true }).click();
    await expect.poll(() => onSave.mock.calls.length).toBe(1);
    await expect.element(page.getByLabelText("Client secret")).toHaveValue("");
    await expect
      .element(page.getByRole("textbox", { name: "Callback URL" }))
      .toHaveValue(callback);
  });
});
