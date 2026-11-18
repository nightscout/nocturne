import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import {
  AlertReplayEventKind,
  type AlertFiringSearchResult,
} from "#lib/api/generated/nocturne-api-client.js";
import { ALERT_PRESETS } from "#lib/test-stubs/alert-presets.js";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const calls = vi.hoisted(() => ({
  created: [] as unknown[],
  searched: [] as unknown[],
  tested: [] as unknown[],
  createError: null as unknown,
  search: null as unknown,
  trackers: [] as unknown[],
}));

vi.mock("#lib/api/generated/alertRules.generated.remote.js", () => ({
  getRules: () => remoteQuery(() => []),
  getPresets: () => remoteQuery(() => ALERT_PRESETS),
  createRule: remoteCommand((body: unknown) => {
    calls.created.push(body);
    return calls.createError ? Promise.reject(calls.createError) : Promise.resolve({ id: "new-id" });
  }),
  testFireDryRun: remoteCommand((body: unknown) => {
    calls.tested.push(body);
    return Promise.resolve({ success: true });
  }),
  getInAppRecipients: () => remoteQuery(() => []),
}));

vi.mock("#lib/api/generated/alertReplays.generated.remote.js", () => ({
  replay: remoteCommand(() => Promise.reject(new Error("not under test"))),
  replayDryRun: remoteCommand(() => Promise.reject(new Error("not under test"))),
  findFirings: remoteCommand((request: unknown) => {
    calls.searched.push(request);
    return Promise.resolve(calls.search);
  }),
}));

vi.mock("#lib/api/generated/linkedPlatforms.generated.remote.js", () => ({
  getLinkedPlatforms: () => remoteQuery(() => ({ platforms: [] })),
}));

vi.mock("#lib/api/generated/systems.generated.remote.js", () => ({
  getChannelStatuses: () =>
    remoteQuery(() => ({
      channels: [{ channelType: "in_app", offered: true, deliversWhileClosed: false }],
    })),
}));

vi.mock("#lib/api/generated/clientDevices.generated.remote.js", () => ({
  getCapabilityCatalog: () => remoteQuery(() => ({ capabilities: [] })),
}));

vi.mock("#lib/api/generated/trackers.generated.remote.js", () => ({
  getDefinitions: () => remoteQuery(() => calls.trackers),
}));

// The chart draws the glucose of the time it is given; the wizard is what is under test.
vi.mock("#lib/components/alerts/wizard/FiringChart.svelte", async () => ({
  default: (await import("#lib/test-stubs/Empty.test-stub.svelte")).default,
}));

import NewAlertPage from "./+page.svelte";

const noFirings: AlertFiringSearchResult = {
  searchedFrom: "2026-04-14T12:00:00Z",
  searchedTo: "2026-04-28T12:00:00Z",
  hasReadings: true,
  total: 0,
  firings: [],
};

const next = () => page.getByRole("button", { name: "Next" });

async function toCheckStep() {
  await page.getByTestId("alert-type-low").click();
  await next().click();
  await next().click();
}

describe("new alert wizard", () => {
  beforeEach(async () => {
    await page.viewport(1280, 900);
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    pageState.state = {};
    calls.created.length = 0;
    calls.searched.length = 0;
    calls.tested.length = 0;
    calls.createError = null;
    calls.search = noFirings;
    calls.trackers = [];
  });

  it("builds a sensor alert on the sensor tracker", async () => {
    calls.trackers = [
      {
        id: "11111111-1111-1111-1111-111111111111",
        name: "Dexcom G7",
        category: "Sensor",
        mode: "Duration",
        lifespanHours: 240,
      },
    ];
    render(NewAlertPage, {});

    await page.getByRole("button", { name: "More alert types" }).click();
    await page.getByTestId("alert-type-sensor_change_due").click();

    await expect
      .element(page.getByLabelText("Alert when the sensor is this old, or older"))
      .toHaveValue(10);
    await expect
      .element(page.getByTestId("alert-tracker-source"))
      .toHaveTextContent("Nocturne reads the age from the tracker Dexcom G7. The tracker lasts 10 days.");

    await page.getByLabelText("Alert when the sensor is this old, or older").fill("9");
    await next().click();
    await next().click();

    await expect.poll(() => calls.searched).toHaveLength(1);
    expect(calls.searched[0]).toMatchObject({
      rule: {
        name: "Dexcom G7 change due: 9 days",
        conditionType: "tracker_age",
        conditionParams: JSON.stringify({
          tracker_definition_id: "11111111-1111-1111-1111-111111111111",
          operator: ">=",
          minutes: 9 * 24 * 60,
        }),
      },
    });
  });

  it("says when there is no sensor tracker and uses the uploader's sensor start", async () => {
    render(NewAlertPage, {});

    await page.getByRole("button", { name: "More alert types" }).click();
    await page.getByTestId("alert-type-sensor_change_due").click();

    await expect
      .element(page.getByTestId("alert-no-tracker"))
      .toHaveTextContent("There is no tracker for the sensor.");
    await expect.element(page.getByRole("link", { name: "Open Trackers" })).toBeVisible();
  });

  it("starts by asking what the alert is for", async () => {
    render(NewAlertPage, {});

    await expect
      .element(page.getByRole("heading", { name: "What is the alert for?" }))
      .toBeVisible();
    await expect.element(page.getByTestId("alert-type-urgent_low")).toBeVisible();
    await expect.element(next()).not.toBeInTheDocument();
  });

  it("goes to the settings of the type that was chosen", async () => {
    render(NewAlertPage, {});

    await page.getByTestId("alert-type-high").click();

    await expect.element(page.getByRole("heading", { name: "When does it alert?" })).toBeVisible();
    await expect.element(page.getByLabelText("Alert when glucose is above")).toHaveValue(250);
  });

  it("does not go on while the level is empty, and says why", async () => {
    render(NewAlertPage, {});

    await page.getByTestId("alert-type-low").click();
    await page.getByLabelText("Alert when glucose is below").fill("");

    await expect.element(next()).toBeDisabled();
    await expect
      .element(page.getByText("Enter a glucose level between 40 and 400 mg/dL."))
      .toBeVisible();
  });

  it("warns that an alert shown only in Nocturne cannot wake anyone", async () => {
    render(NewAlertPage, {});

    await page.getByTestId("alert-type-low").click();
    await next().click();

    await expect.element(page.getByTestId("alert-open-only")).toBeVisible();
  });

  it("checks the alert against recent data when it reaches the last step", async () => {
    calls.search = {
      ...noFirings,
      total: 3,
      firings: [
        { at: "2026-04-28T03:20:00Z", kind: AlertReplayEventKind.Fired },
        { at: "2026-04-26T02:05:00Z", kind: AlertReplayEventKind.Fired },
        { at: "2026-04-20T23:40:00Z", kind: AlertReplayEventKind.Fired },
      ],
    } satisfies AlertFiringSearchResult;
    render(NewAlertPage, {});

    await toCheckStep();

    await expect
      .element(page.getByTestId("alert-history-count"))
      .toHaveTextContent("This alert would have started 3 times in the last 14 days.");
    expect(calls.searched).toHaveLength(1);
    expect(calls.searched[0]).toMatchObject({
      rule: {
        name: "Low: below 70 mg/dL",
        conditionType: "threshold",
        conditionParams: JSON.stringify({ direction: "below", value: 70 }),
      },
    });
  });

  it("says so when the alert would not have started", async () => {
    render(NewAlertPage, {});

    await toCheckStep();

    await expect
      .element(page.getByTestId("alert-history-none"))
      .toHaveTextContent("This alert would not have started in the last 14 days.");
  });

  it("sends a test alert to the places chosen, under the alert's name", async () => {
    render(NewAlertPage, {});

    await toCheckStep();
    await page.getByRole("button", { name: "Send a test alert" }).click();

    await expect.element(page.getByText("Test alert sent. Make sure that it arrived.")).toBeVisible();
    expect(calls.tested[0]).toMatchObject({
      name: "Low: below 70 mg/dL",
      channels: [{ channelType: "in_app" }],
    });
  });

  it("creates the alert under the suggested name", async () => {
    render(NewAlertPage, {});

    await toCheckStep();
    await page.getByRole("button", { name: "Create alert" }).click();

    await expect.poll(() => calls.created).toHaveLength(1);
    expect(calls.created[0]).toMatchObject({
      name: "Low: below 70 mg/dL",
      severity: "warning",
      conditionType: "threshold",
      isEnabled: true,
      autoResolveEnabled: true,
      autoResolveParams: { type: "threshold", threshold: { direction: "above", value: 70 } },
    });
  });

  it("says on the last step what closes the alert", async () => {
    render(NewAlertPage, {});

    await toCheckStep();

    await expect
      .element(page.getByTestId("alert-auto-resolve"))
      .toHaveTextContent("The alert closes when this is true: Glucose above 70 mg/dL.");
  });

  it("keeps a name the person typed", async () => {
    render(NewAlertPage, {});

    await toCheckStep();
    await page.getByLabelText("Name").fill("Night low");
    await page.getByRole("button", { name: "Create alert" }).click();

    await expect.poll(() => calls.created).toHaveLength(1);
    expect(calls.created[0]).toMatchObject({ name: "Night low" });
  });

  it("asks the user to try again when the alert cannot be created", async () => {
    calls.createError = new Error("network down");
    render(NewAlertPage, {});

    await toCheckStep();
    await page.getByRole("button", { name: "Create alert" }).click();

    await expect.element(page.getByText("Could not create the alert. Try again.")).toBeVisible();
  });

  it("goes back a step with what was set still in place", async () => {
    render(NewAlertPage, {});

    await page.getByTestId("alert-type-low").click();
    await page.getByLabelText("Alert when glucose is below").fill("80");
    await next().click();
    await page.getByRole("button", { name: "Back" }).click();

    await expect.element(page.getByLabelText("Alert when glucose is below")).toHaveValue(80);
  });
});
