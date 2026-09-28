import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

const calls = vi.hoisted(() => ({ saved: 0, activated: 0, failActivation: false }));
const grant = vi.hoisted(() => ({
  scopes: ["tenant.settings"] as string[],
  refusedAsDemoSubject: false,
}));

vi.mock("$lib/api/generated/myPermissions.generated.remote", () => ({
  getMyPermissions: () => ({ current: { ...grant }, loading: false }),
}));

vi.mock("$lib/api/generated/configurations.generated.remote", () => ({
  getAllConnectorStatus: () => ({ current: [], loading: false }),
  getSchema: () => ({
    loading: false,
    current: {
      type: "object",
      properties: {
        url: { type: "string", title: "URL", default: "https://old.example" },
      },
    },
  }),
  getConfiguration: () => ({ current: null, loading: false }),
  getEffectiveConfiguration: () => ({ current: {}, loading: false }),
  saveConfiguration: () => {
    calls.saved++;
    return Promise.resolve({});
  },
  saveSecrets: () => Promise.resolve({}),
  setActive: () => {
    calls.activated++;
    return calls.failActivation ? Promise.reject(new Error("activation failed")) : Promise.resolve({});
  },
  deleteConfiguration: () => Promise.resolve({}),
}));

vi.mock("$lib/api/generated/services.generated.remote", () => ({
  getServicesOverview: () => ({
    loading: false,
    current: {
      availableConnectors: [{ id: "nightscout", name: "Nightscout" }],
    },
  }),
  getConnectorCapabilities: () => ({ current: {}, loading: false }),
  getConnectorDataSummary: () => ({ current: { total: 0 }, loading: false }),
  deleteConnectorData: () => Promise.resolve({}),
}));

vi.mock("$lib/api/generated/careLinkConnects.generated.remote", () => ({
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
});
