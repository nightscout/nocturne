import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

const calls = vi.hoisted(() => ({ saved: 0, activated: 0, failActivation: false }));
const capabilities = vi.hoisted(() => ({ savedSecretStopsAt: undefined as string | undefined }));
const secretsSave = vi.hoisted(() => ({
  withSecretField: false,
  result: {} as { secretAlreadyInUse?: boolean; secretInUseStopsAt?: string },
}));
const grant = vi.hoisted(() => ({
  scopes: ["tenant.settings"] as string[],
  refusedAsDemoSubject: false,
}));

vi.mock("#lib/api/generated/myPermissions.generated.remote.js", () => ({
  getMyPermissions: () => ({ current: { ...grant }, loading: false }),
}));

vi.mock("#lib/api/generated/configurations.generated.remote.js", () => ({
  getAllConnectorStatus: () => ({ current: [], loading: false }),
  getSchema: () => ({
    loading: false,
    current: {
      type: "object",
      properties: {
        url: { type: "string", title: "URL", default: "https://old.example" },
        ...(secretsSave.withSecretField
          ? { apiSecret: { type: "string", title: "API secret", "x-secret": true } }
          : {}),
      },
      ...(secretsSave.withSecretField ? { secrets: ["apiSecret"] } : {}),
    },
  }),
  getConfiguration: () => ({ current: null, loading: false }),
  getEffectiveConfiguration: () => ({ current: {}, loading: false }),
  saveConfiguration: () => {
    calls.saved++;
    return Promise.resolve({});
  },
  saveSecrets: () => Promise.resolve(secretsSave.result),
  setActive: () => {
    calls.activated++;
    return calls.failActivation ? Promise.reject(new Error("activation failed")) : Promise.resolve({});
  },
  deleteConfiguration: () => Promise.resolve({}),
}));

vi.mock("#lib/api/generated/services.generated.remote.js", () => ({
  getServicesOverview: () => ({
    loading: false,
    current: {
      availableConnectors: [{ id: "nightscout", name: "Nightscout" }],
    },
  }),
  getConnectorCapabilities: () => ({
    current: {
      supportedDataTypes: ["Glucose", "Treatments"],
      supportsHistoricalSync: true,
      maxHistoricalDays: 90,
      supportsManualSync: false,
      savedSecretStopsAt: capabilities.savedSecretStopsAt,
    },
    loading: false,
  }),
  getConnectorDataSummary: () => ({ current: { total: 0 }, loading: false }),
  deleteConnectorData: () => Promise.resolve({}),
}));

vi.mock("#lib/api/generated/careLinkConnects.generated.remote.js", () => ({
  start: () => Promise.resolve({}),
  complete: () => Promise.resolve({}),
  desktopToken: () => Promise.resolve({}),
}));

import ConnectorSetup from "./ConnectorSetup.svelte";

async function save(primaryAction: "save-and-finish" | "save-only") {
  const onComplete = vi.fn();

  render(ConnectorSetup, {
    props: { connectorId: "nightscout", primaryAction, onComplete },
  });

  await page.getByRole("textbox").first().fill("https://new.example");
  await page.getByRole("button", { name: "Save" }).click();
  await expect.poll(() => calls.saved).toBe(1);

  return onComplete;
}

describe("ConnectorSetup", () => {
  beforeEach(() => {
    calls.saved = 0;
    calls.activated = 0;
    calls.failActivation = false;
    grant.scopes = ["tenant.settings"];
    grant.refusedAsDemoSubject = false;
    capabilities.savedSecretStopsAt = undefined;
    secretsSave.result = {};
    secretsSave.withSecretField = false;
  });

  it("says when the credential that already holds a saved secret stops, in place of the promise", async () => {
    capabilities.savedSecretStopsAt = "2026-12-20T00:00:00Z";
    secretsSave.withSecretField = true;
    secretsSave.result = { secretAlreadyInUse: true, secretInUseStopsAt: "2026-12-02T00:00:00Z" };
    render(ConnectorSetup, { props: { connectorId: "nightscout", primaryAction: "save-only" } });

    await page.getByRole("textbox").first().fill("https://new.example");
    await page.getByPlaceholder("Enter to update (leave blank to keep current)").fill("the-site-secret");
    await page.getByRole("button", { name: "Save" }).click();

    await expect
      .element(page.getByTestId("connector-secret-in-use"))
      .toHaveTextContent(/^This secret is already in use\. It stops on .+\.$/);
    expect(page.getByTestId("connector-secret-stops").elements()).toHaveLength(0);
  });

  it("says nothing new when the credential that already holds the secret does not stop", async () => {
    capabilities.savedSecretStopsAt = "2026-12-20T00:00:00Z";
    secretsSave.withSecretField = true;
    secretsSave.result = { secretAlreadyInUse: true };
    render(ConnectorSetup, { props: { connectorId: "nightscout", primaryAction: "save-only" } });

    await page.getByRole("textbox").first().fill("https://new.example");
    await page.getByPlaceholder("Enter to update (leave blank to keep current)").fill("the-site-secret");
    await page.getByRole("button", { name: "Save" }).click();
    await expect.poll(() => calls.saved).toBe(1);

    await expect.element(page.getByTestId("connector-secret-stops")).toBeVisible();
    expect(page.getByTestId("connector-secret-in-use").elements()).toHaveLength(0);
  });

  it("tells a member whose access ends that a saved secret stops then, before they save", async () => {
    capabilities.savedSecretStopsAt = "2026-12-02T00:00:00Z";
    render(ConnectorSetup, { props: { connectorId: "nightscout", primaryAction: "save-only" } });

    await expect
      .element(page.getByTestId("connector-secret-stops"))
      .toHaveTextContent(/^Your access ends on .+\. This credential stops then\.$/);
    await expect.element(page.getByRole("textbox").first()).toBeVisible();
  });

  it("says nothing about a stop when the API gives none", async () => {
    render(ConnectorSetup, { props: { connectorId: "nightscout", primaryAction: "save-only" } });

    await expect.element(page.getByRole("textbox").first()).toBeVisible();
    expect(page.getByTestId("connector-secret-stops").elements()).toHaveLength(0);
  });

  it("hands the setup wizard back its flow once the save succeeds", async () => {
    const onComplete = await save("save-and-finish");

    await expect.poll(() => calls.activated).toBe(1);
    expect(onComplete).toHaveBeenCalledTimes(1);
  });

  it("does not report completion when activation fails", async () => {
    calls.failActivation = true;
    const onComplete = await save("save-and-finish");

    await expect.poll(() => calls.activated).toBe(1);
    expect(onComplete).not.toHaveBeenCalled();
  });

  it("keeps the user on the form when managing an existing connector", async () => {
    const onComplete = await save("save-only");

    expect(calls.activated).toBe(0);
    expect(onComplete).not.toHaveBeenCalled();
  });

  describe("controls", () => {
    function renderManaged() {
      render(ConnectorSetup, {
        props: { connectorId: "nightscout", showToggle: true },
      });
    }

    async function expectReadOnly() {
      await expect.element(page.getByTestId("connector-read-only")).toBeVisible();
      expect(page.getByRole("switch").elements()).toHaveLength(0);
      expect(page.getByRole("textbox").elements()).toHaveLength(0);
      expect(page.getByRole("button", { name: "Save" }).elements()).toHaveLength(0);
    }

    it("offers the form and the enable switch to a member holding tenant.settings", async () => {
      renderManaged();

      await expect.element(page.getByRole("switch")).toBeVisible();
      await expect.element(page.getByRole("textbox").first()).toBeVisible();
      expect(page.getByTestId("connector-read-only").elements()).toHaveLength(0);
    });

    it("shows a member without tenant.settings no form and no switch", async () => {
      grant.scopes = ["glucose.read", "treatments.readwrite"];
      renderManaged();

      await expectReadOnly();
    });

    it("shows the demo visitor no form and no switch despite its tenant.settings", async () => {
      grant.refusedAsDemoSubject = true;
      renderManaged();

      await expectReadOnly();
    });
  });

  it("lists what the connector can sync when capabilities are shown", async () => {
    render(ConnectorSetup, {
      props: { connectorId: "nightscout", showCapabilities: true },
    });

    await expect.element(page.getByText("Sync capabilities")).toBeVisible();
    await expect.element(page.getByText("Glucose", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Treatments", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Supported", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Disabled", { exact: true })).toBeVisible();
    await expect.element(page.getByText("90", { exact: true })).toBeVisible();
  });
});
