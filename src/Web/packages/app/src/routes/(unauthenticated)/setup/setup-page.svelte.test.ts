import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

// Only the wizard's own navigation is under test; every step body it can reach is stood down.
const { markSetupComplete } = vi.hoisted(() => ({ markSetupComplete: vi.fn() }));
vi.mock("./setup.remote", () => ({ markSetupComplete }));
vi.mock("@nocturne/watercolour", async () => ({
  Artwork: (await import("$lib/test-stubs/Artwork.test-stub.svelte")).default,
  hostSurface: () => "light",
  watchSurface: () => () => {},
}));
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
vi.mock("./steps/TenantIdentity.svelte", emptyStub);
vi.mock("./steps/AccountCreation.svelte", emptyStub);
vi.mock("./steps/NightscoutConnect.svelte", async () => ({
  default: (await import("$lib/test-stubs/Complete.test-stub.svelte")).default,
}));
vi.mock("./steps/ImportProgress.svelte", async () => ({
  default: (await import("$lib/test-stubs/ImportProgress.test-stub.svelte")).default,
}));
vi.mock("./steps/Finish.svelte", emptyStub);
vi.mock("$lib/components/connectors/DataSourceSelectionView.svelte", emptyStub);
vi.mock("$lib/components/connectors/ConnectorSetup.svelte", emptyStub);
vi.mock("$lib/components/connectors/UploaderSetupView.svelte", emptyStub);

import { startFromConnector } from "$api/generated/migrations.generated.remote";
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

describe("setup chrome", () => {
  beforeEach(() => markSetupComplete.mockClear());

  it("follows the person's theme instead of forcing dark", async () => {
    const { container } = render(SetupPage);

    await expect.element(freshCard()).toBeVisible();
    expect(container.querySelector(".dark")).toBeNull();
  });

  it("shows each step's artwork beside it", async () => {
    render(SetupPage);

    const artwork = page.getByTestId("artwork");
    await expect.element(artwork).toHaveAttribute("data-artwork", "crescent-moon");

    await continueButton().click();

    await expect.element(artwork).toHaveAttribute("data-artwork", "plug");
  });

  // Leaving marks onboarding complete on the server, but nothing past the
  // steps actually finished is set up, so the control must not say otherwise.
  it("offers an exit that claims nothing was saved", async () => {
    render(SetupPage);

    await expect.element(page.getByRole("button", { name: /Save/ })).not.toBeInTheDocument();
    await page.getByRole("button", { name: "Exit setup" }).click();

    expect(markSetupComplete).toHaveBeenCalledOnce();
  });

  it("has no action on the data source step that pretends to save", async () => {
    render(SetupPage);

    await continueButton().click();

    await expect
      .element(page.getByRole("heading", { name: /Connect a data source/ }))
      .toBeVisible();
    await expect
      .element(page.getByRole("button", { name: "Save and continue" }))
      .not.toBeInTheDocument();
  });
});

describe("setup import step", () => {
  async function reachImport() {
    render(SetupPage);
    await migrationCard().click();
    await continueButton().click();
    await page.getByRole("button", { name: "Skip for now" }).click();
    await expect.element(page.getByText("Step 03 / 04")).toBeVisible();
  }

  it("blocks leaving while the import runs", async () => {
    await reachImport();

    await expect.element(continueButton()).not.toBeInTheDocument();
    await expect
      .element(page.getByRole("button", { name: "Skip for now" }))
      .not.toBeInTheDocument();
    await expect
      .element(page.getByRole("button", { name: "Back" }))
      .not.toBeInTheDocument();

    await page.getByRole("button", { name: "Finish" }).click();

    await expect.element(page.getByText("Step 03 / 04")).toBeVisible();
  });

  it("offers Continue once the import has settled", async () => {
    await reachImport();

    await page.getByRole("button", { name: "Stub: settle" }).click();
    await continueButton().click();

    await expect.element(page.getByText("Step 04 / 04")).toBeVisible();
  });

  it("hands the import step the reason a run could not be started", async () => {
    vi.mocked(startFromConnector).mockRejectedValueOnce(new Error("refused"));
    render(SetupPage);
    await migrationCard().click();
    await continueButton().click();

    await page.getByRole("button", { name: "Stub: complete" }).click();

    await expect.element(page.getByText("Step 03 / 04")).toBeVisible();
    await expect.element(page.getByTestId("start-error")).not.toBeEmptyDOMElement();
  });
});
