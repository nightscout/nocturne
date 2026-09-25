import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

// Only the wizard's own navigation is under test; every step body it can reach is stood down.
vi.mock("./setup.remote", () => ({ markSetupComplete: vi.fn() }));
vi.mock("$api/generated/migrations.generated.remote", () => ({
  getHistory: () => remoteQuery(() => []),
  startFromConnector: vi.fn(),
}));
vi.mock("$api/generated/services.generated.remote", () => ({
  getServicesOverview: () => remoteQuery(() => null),
  getActiveDataSources: () => remoteQuery(() => []),
  getUploaderSetup: () => remoteQuery(() => null),
}));
const { emptyStub } = vi.hoisted(() => ({
  emptyStub: async () => ({
    default: (await import("$lib/test-stubs/Empty.test-stub.svelte")).default,
  }),
}));
vi.mock("./ConstellationCanvas.svelte", emptyStub);
vi.mock("./steps/TenantIdentity.svelte", emptyStub);
vi.mock("./steps/AccountCreation.svelte", emptyStub);
vi.mock("./steps/NightscoutConnect.svelte", emptyStub);
vi.mock("./steps/ImportProgress.svelte", emptyStub);
vi.mock("./steps/Finish.svelte", emptyStub);
vi.mock("$lib/components/connectors/DataSourceSelectionView.svelte", emptyStub);
vi.mock("$lib/components/connectors/ConnectorSetup.svelte", emptyStub);
vi.mock("$lib/components/connectors/UploaderSetupView.svelte", emptyStub);

import SetupPage from "./+page@.svelte";

const freshCard = () =>
  page.getByRole("radio", { name: /Start with a blank slate/ });
const migrationCard = () =>
  page.getByRole("radio", { name: /Migrate my Nightscout data/ });
const continueButton = () => page.getByRole("button", { name: "Continue" });

describe("setup path step", () => {
  it("starts on the fresh path", async () => {
    render(SetupPage);

    await expect.element(freshCard()).toHaveAttribute("aria-checked", "true");
    await expect
      .element(page.getByText("Fresh Start", { exact: true }))
      .toBeVisible();
  });

  it("stays on the path step when a card is selected", async () => {
    render(SetupPage);

    await migrationCard().click();

    await expect
      .element(migrationCard())
      .toHaveAttribute("aria-checked", "true");
    await expect.element(page.getByText("Step 01 / 04")).toBeVisible();
    await expect
      .element(page.getByText("Nightscout Migration", { exact: true }))
      .toBeVisible();
  });

  it("advances along the chosen path on Continue", async () => {
    render(SetupPage);

    await migrationCard().click();
    await continueButton().click();

    await expect.element(page.getByText("Step 02 / 04")).toBeVisible();
    await expect
      .element(page.getByText("Nightscout Migration", { exact: true }))
      .toBeVisible();
    await expect.element(migrationCard()).not.toBeInTheDocument();
  });

  it("advances the fresh path to its data source step", async () => {
    render(SetupPage);

    await continueButton().click();

    await expect
      .element(page.getByRole("heading", { name: /Connect a data source/ }))
      .toBeVisible();
  });

  it("keeps the chosen path when going back", async () => {
    render(SetupPage);

    await migrationCard().click();
    await continueButton().click();
    await page.getByRole("button", { name: "Back" }).click();

    await expect
      .element(migrationCard())
      .toHaveAttribute("aria-checked", "true");
    await expect.element(freshCard()).toHaveAttribute("aria-checked", "false");
  });
});
