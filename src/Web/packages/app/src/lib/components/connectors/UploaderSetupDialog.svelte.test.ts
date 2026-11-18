import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { UploaderPlatform, type UploaderApp } from "#lib/api/generated/nocturne-api-client.js";

const { create } = vi.hoisted(() => ({ create: vi.fn() }));

vi.mock("#lib/api/generated/directGrants.generated.remote.js", () => ({
  create: (...args: unknown[]) => create(...args),
}));

import UploaderSetupDialog from "./UploaderSetupDialog.svelte";

const AAPS: UploaderApp = { id: "aaps", platform: UploaderPlatform.Android };
const JUGGLUCO: UploaderApp = { id: "juggluco", platform: UploaderPlatform.Android };

describe("UploaderSetupDialog API key", () => {
  beforeEach(() => {
    create.mockReset();
    create.mockResolvedValue({ token: "noc_created" });
  });

  it("shows the generated key in the instructions", async () => {
    render(UploaderSetupDialog, { open: true, selectedUploader: AAPS });

    await page.getByRole("button", { name: "Generate API key" }).click();

    await expect.element(page.getByText("noc_created")).toBeVisible();
    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({ scopes: ["health.readwrite"] }),
    );
    await expect
      .element(page.getByRole("button", { name: "Generate API key" }))
      .not.toBeInTheDocument();
  });

  it("tells an AAPS user where the URL and key go", async () => {
    render(UploaderSetupDialog, { open: true, selectedUploader: AAPS });

    await expect.element(page.getByText(/NS access token/)).toBeVisible();
  });

  it("makes a read-only QR code for AAPSClient", async () => {
    render(UploaderSetupDialog, { open: true, selectedUploader: AAPS });

    await page.getByRole("button", { name: "Make QR code" }).click();

    await expect.element(page.getByRole("img", { name: "QR code to connect AAPSClient" })).toBeVisible();
    expect(create).toHaveBeenCalledWith({ label: "AAPSClient", scopes: ["health.read"] });
    await expect.element(page.getByText("This phone can only read your data.")).toBeVisible();
    await expect.element(page.getByText("noc_created")).not.toBeInTheDocument();
  });

  it("adds the scope AAPSClient commands need when the user asks for them", async () => {
    render(UploaderSetupDialog, { open: true, selectedUploader: AAPS });

    await page.getByRole("switch", { name: "Let this phone send commands to your AAPS phone" }).click();
    await page.getByRole("button", { name: "Make QR code" }).click();

    await expect.element(page.getByText(/send commands to your AAPS phone\./)).toBeVisible();
    expect(create).toHaveBeenCalledWith({
      label: "AAPSClient",
      scopes: ["health.read", "therapy.readwrite"],
    });
  });

  it("offers the AAPSClient QR code only for AAPS", async () => {
    render(UploaderSetupDialog, { open: true, selectedUploader: JUGGLUCO });

    await expect.element(page.getByRole("button", { name: "Generate API key" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Make QR code" })).not.toBeInTheDocument();
  });

  it("keeps the reason when the key cannot be created", async () => {
    create.mockRejectedValue({ status: 400, body: { message: "Label already used." } });
    render(UploaderSetupDialog, { open: true, selectedUploader: JUGGLUCO });

    await page.getByRole("button", { name: "Generate API key" }).click();

    await expect.element(page.getByText("Label already used.")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Generate API key" })).toBeVisible();
  });

  it("links to the uploader's website", async () => {
    render(UploaderSetupDialog, {
      open: true,
      selectedUploader: { ...JUGGLUCO, url: "https://juggluco.example" },
    });

    await expect
      .element(page.getByRole("link", { name: "Visit Juggluco website" }))
      .toHaveAttribute("href", "https://juggluco.example");
  });
});
