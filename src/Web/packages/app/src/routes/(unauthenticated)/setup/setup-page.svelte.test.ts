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
const relationship = vi.hoisted(() => ({
  stored: {} as { relationship?: string; patientName?: string },
  set: vi.fn(),
}));
vi.mock("$api/generated/tenantSettings.generated.remote", () => ({
  getPatientRelationship: () => remoteQuery(() => relationship.stored),
  setPatientRelationship: relationship.set,
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
const skipButton = () => page.getByRole("button", { name: "Skip for now" });

async function renderAtPath() {
  render(SetupPage);
  await skipButton().click();
  await expect.element(freshCard()).toBeVisible();
}

const sendingCopy = () => page.getByText(/Choose a cloud service/);

beforeEach(() => {
  relationship.stored = {};
  relationship.set.mockReset();
});

describe("setup who-for step", () => {
  it("asks who Nocturne is for first, as a named radio group", async () => {
    render(SetupPage);

    const group = page.getByRole("radiogroup", { name: /Who is Nocturne for/ });
    await expect.element(group).toBeVisible();
    expect(group.getByRole("radio").elements()).toHaveLength(3);
    await expect.element(page.getByText("Step 01 / 05")).toBeVisible();
    await expect
      .element(page.getByTestId("artwork"))
      .toHaveAttribute("data-artwork", "people-group");
  });

  it("asks the patient's name only for someone else", async () => {
    render(SetupPage);
    const name = page.getByLabelText("What's their name?");

    await page.getByRole("radio", { name: /^Me/ }).click();
    await expect.element(name).not.toBeInTheDocument();

    await page.getByRole("radio", { name: /Someone I care for/ }).click();
    await expect.element(name).toBeVisible();

    await page.getByRole("radio", { name: /setting it up for someone else/ }).click();
    await expect.element(name).toBeVisible();
  });

  it.each([
    ["Someone I care for", "Caregiver"],
    ["setting it up for someone else", "Helper"],
  ])("saves %s with the name and speaks of them by it", async (label, value) => {
    render(SetupPage);

    await page.getByRole("radio", { name: new RegExp(label) }).click();
    await page.getByLabelText("What's their name?").fill(" Sam ");
    await continueButton().click();

    expect(relationship.set).toHaveBeenCalledWith({ relationship: value, patientName: "Sam" });
    await continueButton().click();
    await expect.element(sendingCopy()).toHaveTextContent(/sending Sam's glucose/);
  });

  it("speaks to the patient when it is for me", async () => {
    render(SetupPage);

    await page.getByRole("radio", { name: /^Me/ }).click();
    await continueButton().click();

    expect(relationship.set).toHaveBeenCalledWith({ relationship: "Self", patientName: undefined });
    await continueButton().click();
    await expect.element(sendingCopy()).toHaveTextContent(/sending your glucose/);
  });

  it("stays neutral and saves nothing when skipped", async () => {
    render(SetupPage);

    await page.getByRole("radio", { name: /Someone I care for/ }).click();
    await skipButton().click();
    await continueButton().click();

    expect(relationship.set).not.toHaveBeenCalled();
    await expect.element(sendingCopy()).toHaveTextContent(/sending the glucose/);
  });

  it("keeps the tenant's earlier answer", async () => {
    relationship.stored = { relationship: "Caregiver", patientName: "Sam" };
    render(SetupPage);

    await expect
      .element(page.getByRole("radio", { name: /Someone I care for/ }))
      .toHaveAttribute("aria-checked", "true");
    await expect.element(page.getByLabelText("What's their name?")).toHaveValue("Sam");
  });

  it("stays on the step when the answer cannot be saved", async () => {
    relationship.set.mockRejectedValueOnce(new Error("boom"));
    render(SetupPage);

    await page.getByRole("radio", { name: /^Me/ }).click();
    await continueButton().click();

    await expect.element(page.getByText("Step 01 / 05")).toBeVisible();
    await expect.element(page.getByRole("alert")).toBeVisible();
  });
});

describe("setup path step", () => {
  it("starts on the fresh path", async () => {
    await renderAtPath();

    await expect.element(freshCard()).toHaveAttribute("aria-checked", "true");
    await expect
      .element(page.getByText("Fresh Start", { exact: true }))
      .toBeVisible();
  });

  it("stays on the path step when a card is selected", async () => {
    await renderAtPath();

    await migrationCard().click();

    await expect
      .element(migrationCard())
      .toHaveAttribute("aria-checked", "true");
    await expect.element(page.getByText("Step 02 / 05")).toBeVisible();
    await expect
      .element(page.getByText("Nightscout Migration", { exact: true }))
      .toBeVisible();
  });

  it("advances along the chosen path on Continue", async () => {
    await renderAtPath();

    await migrationCard().click();
    await continueButton().click();

    await expect.element(page.getByText("Step 03 / 05")).toBeVisible();
    await expect
      .element(page.getByText("Nightscout Migration", { exact: true }))
      .toBeVisible();
    await expect.element(migrationCard()).not.toBeInTheDocument();
  });

  it("advances the fresh path to its data source step", async () => {
    await renderAtPath();

    await continueButton().click();

    await expect
      .element(page.getByRole("heading", { name: /Connect a data source/ }))
      .toBeVisible();
  });

  it("keeps the chosen path when going back", async () => {
    await renderAtPath();

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

    await skipButton().click();
    await expect.element(freshCard()).toBeVisible();
    expect(container.querySelector(".dark")).toBeNull();
  });

  it("shows each step's artwork beside it", async () => {
    await renderAtPath();

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
    await renderAtPath();

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
    await renderAtPath();
    await migrationCard().click();
    await continueButton().click();
    await skipButton().click();
    await expect.element(page.getByText("Step 04 / 05")).toBeVisible();
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

    await expect.element(page.getByText("Step 04 / 05")).toBeVisible();
  });

  it("offers Continue once the import has settled", async () => {
    await reachImport();

    await page.getByRole("button", { name: "Stub: settle" }).click();
    await continueButton().click();

    await expect.element(page.getByText("Step 05 / 05")).toBeVisible();
  });

  it("words a refused start as a connection that isn't saved", async () => {
    vi.mocked(startFromConnector).mockRejectedValueOnce({
      status: 400,
      body: { message: "Nightscout URL not found in connector configuration" },
    });
    await renderAtPath();
    await migrationCard().click();
    await continueButton().click();

    await page.getByRole("button", { name: "Stub: complete" }).click();

    await expect.element(page.getByText("Step 04 / 05")).toBeVisible();
    await expect
      .element(page.getByTestId("start-error"))
      .toHaveTextContent("Your Nightscout connection isn't saved yet.");
  });
});
