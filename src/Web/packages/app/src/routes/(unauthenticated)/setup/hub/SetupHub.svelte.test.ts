import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { SetupHubStatus } from "#lib/api/index.js";

vi.mock("water-ui", async () => ({
  Artwork: (await import("#lib/test-stubs/Artwork.test-stub.svelte")).default,
  DropSurface: (await import("#lib/test-stubs/DropSurface.test-stub.svelte")).default,
  DropGroup: (await import("#lib/test-stubs/Passthrough.test-stub.svelte")).default,
  ConfirmationBackground: (await import("#lib/test-stubs/Empty.test-stub.svelte")).default,
  hostSurface: () => "light",
  watchSurface: () => () => {},
  prefersReducedMotion: () => false,
}));

const hub = $state({ status: undefined as unknown });
vi.mock("#lib/api/generated/setupHubs.generated.remote.js", () => ({
  getSetupHub: () => remoteQuery(() => hub.status),
}));

const tenant = vi.hoisted(() => ({
  relationship: {} as { relationship?: string; patientName?: string },
  setUnits: vi.fn(),
  setRelationship: vi.fn(),
}));
vi.mock("#lib/api/generated/tenantSettings.generated.remote.js", () => ({
  getPatientRelationship: () => remoteQuery(() => tenant.relationship),
  setPatientRelationship: tenant.setRelationship,
  getUnitsAndTimezone: () =>
    remoteQuery(() => ({ glucoseUnits: "mmol", timezone: "Pacific/Auckland" })),
  setUnitsAndTimezone: tenant.setUnits,
}));
const members = vi.hoisted(() => ({ stepDown: vi.fn(), goto: vi.fn() }));
vi.mock("#lib/api/generated/memberInvites.generated.remote.js", () => ({
  stepDown: members.stepDown,
}));
vi.mock("#lib/api/generated/roles.generated.remote.js", () => ({
  getRoles: () =>
    remoteQuery(() => [
      { id: "role-caretaker", slug: "caretaker", name: "Caretaker", accessLevel: "ReadAndLogTreatments" },
    ]),
}));
vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  goto: members.goto,
}));
vi.mock("#lib/stores/appearance-store.svelte.js", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  applyPreferences: vi.fn(),
}));

import { SetupHubItemKey, SetupHubItemState } from "#lib/api/index.js";
import SetupHub from "./SetupHub.svelte";

const KEYS = Object.values(SetupHubItemKey);

function hubWith(resolved: number): SetupHubStatus {
  const items = KEYS.map((key, i) => ({
    key,
    state:
      i >= resolved
        ? SetupHubItemState.Open
        : i % 2 === 0
          ? SetupHubItemState.Done
          : SetupHubItemState.NotForMe,
  }));
  return {
    items,
    resolvedCount: resolved,
    openCount: KEYS.length - resolved,
    totalCount: KEYS.length,
    revision: `r${resolved}`,
    showStrip: resolved < KEYS.length,
  };
}

beforeEach(() => {
  sessionStorage.clear();
  tenant.relationship = {};
  tenant.setUnits.mockReset().mockResolvedValue({});
  tenant.setRelationship.mockReset().mockResolvedValue({});
  members.stepDown.mockReset().mockResolvedValue({ success: true });
  members.goto.mockReset().mockResolvedValue(undefined);
});

function handingOver(handover: SetupHubStatus["handover"]): SetupHubStatus {
  return { ...hubWith(2), handover };
}

describe("SetupHub", () => {
  it("lists every item with where it stands, and counts the resolved ones", async () => {
    hub.status = hubWith(3);
    render(SetupHub);

    await expect.element(page.getByTestId("hub-progress")).toHaveTextContent(/3 of 6 set up/);
    const done = page.getByTestId("hub-item-connect-data");
    await expect.element(done).toHaveAttribute("data-state", "Done");
    await expect.element(done).toHaveAttribute("data-mark-shown", "false");
    await expect.element(done.getByText("Done")).toBeVisible();

    const setAside = page.getByTestId("hub-item-alerts");
    await expect.element(setAside.getByText("Not for me")).toBeVisible();
    await expect.element(setAside).not.toHaveAttribute("data-mark-shown");

    await expect.element(page.getByTestId("hub-item-about")).toHaveAttribute("data-state", "Open");
  });

  it("paints one stop per resolved item", async () => {
    const { fakePlayer } = await import("#lib/test-stubs/Artwork.test-stub.svelte");
    fakePlayer.seeks = [];
    hub.status = hubWith(4);
    render(SetupHub);

    await expect.poll(() => fakePlayer.seeks.at(-1)).toBe(4 / 6);
  });

  it("speaks of the patient by name", async () => {
    hub.status = hubWith(0);
    tenant.relationship = { relationship: "Caregiver", patientName: "Sam" };
    render(SetupHub);

    await expect.element(page.getByRole("heading", { name: "Set Nocturne up for Sam." })).toBeVisible();
    await expect
      .element(page.getByText("Choose who else can see Sam's data."))
      .toBeVisible();
    await expect.element(page.getByTestId("basics-who")).toHaveTextContent("Sam, someone you care for");
  });

  it("opens a finished hub collapsed to all set up", async () => {
    hub.status = hubWith(6);
    render(SetupHub);

    await expect.element(page.getByTestId("hub-all-set")).toBeVisible();
    await expect.element(page.getByTestId("hub-header")).not.toBeInTheDocument();
  });

  it("paints the last stage, then washes, then collapses, when it was last seen unfinished", async () => {
    const { fakePlayer } = await import("#lib/test-stubs/Artwork.test-stub.svelte");
    Object.assign(fakePlayer.state, { mode: "live", motion: "full", progress: 0, playing: false });
    sessionStorage.setItem("nocturne.setup-hub.stop", "5");
    hub.status = hubWith(6);
    render(SetupHub);

    await expect.poll(() => fakePlayer.state.playing).toBe(true);
    // Longer than the wash: the collapse waits on the painting, not on a clock.
    await new Promise((resolve) => setTimeout(resolve, 3500));
    await expect.element(page.getByTestId("hub-header")).toBeVisible();

    fakePlayer.state.progress = 1;
    fakePlayer.present();
    fakePlayer.present();

    await expect
      .poll(() => page.getByTestId("hub-all-set").query(), { timeout: 5000 })
      .not.toBeNull();
  });

  it("edits the units from Basics, saying which unit therapy settings are entered in", async () => {
    hub.status = hubWith(2);
    render(SetupHub);

    await expect.element(page.getByTestId("basics-units")).toHaveTextContent(/mmol\/L/);
    await expect.element(page.getByText(/are entered in this unit/)).toBeVisible();

    await page.getByRole("button", { name: "Change" }).nth(1).click();
    await page.getByRole("radio", { name: /mg\/dL/ }).click();
    await page.getByRole("button", { name: "Save" }).click();

    expect(tenant.setUnits).toHaveBeenCalledWith({
      glucoseUnits: "mg/dl",
      timezone: "Pacific/Auckland",
    });
  });

  describe("handed over by a helper", () => {
    const recipient = {
      helperName: "Alex",
      callerIsHelper: false,
      recipientJoined: true,
      recipientAnswered: false,
      canStepDown: false,
    };

    beforeEach(() => {
      tenant.relationship = { relationship: "Helper", patientName: "Sam" };
    });

    it("tells the recipient who set it up, and asks who it is for without the helper's answer", async () => {
      hub.status = handingOver(recipient);
      render(SetupHub);

      await expect
        .element(page.getByRole("heading", { name: "Alex set up Nocturne for you." }))
        .toBeVisible();
      await expect.element(page.getByText("A few things only you can decide.")).toBeVisible();
      const question = page.getByTestId("hub-recipient-question");
      await expect.element(question.getByRole("radio", { name: /^Me/ })).toBeVisible();
      await expect.element(question.getByRole("radio", { name: /Someone I care for/ })).toBeVisible();
      expect(question.getByRole("radio", { name: /setting it up for someone else/ }).elements()).toHaveLength(0);
      await expect.element(page.getByTestId("basics-who")).toHaveTextContent("Not answered yet");
      await expect.element(page.getByTestId("hub-item-connect-data")).toHaveAttribute("data-state", "Done");
      expect(page.getByTestId("hub-step-down").query()).toBeNull();
    });

    it("saves the recipient's answer over the helper's, and keeps the ticks", async () => {
      hub.status = handingOver(recipient);
      tenant.setRelationship.mockImplementation(async () => {
        hub.status = handingOver({ ...recipient, recipientAnswered: true });
        return {};
      });
      render(SetupHub);

      const question = page.getByTestId("hub-recipient-question");
      await question.getByRole("radio", { name: /^Me/ }).click();
      await question.getByRole("button", { name: "Save" }).click();

      await vi.waitFor(() =>
        expect(tenant.setRelationship).toHaveBeenCalledWith(expect.objectContaining({ relationship: "Self" })),
      );
      await expect.poll(() => page.getByTestId("hub-recipient-question").query()).toBeNull();
      await expect.element(page.getByTestId("hub-item-connect-data")).toHaveAttribute("data-state", "Done");
    });

    it("stops asking once the recipient has answered", async () => {
      hub.status = handingOver({ ...recipient, recipientAnswered: true });
      tenant.relationship = { relationship: "Self" };
      render(SetupHub);

      await expect.element(page.getByRole("heading", { name: "Make Nocturne yours." })).toBeVisible();
      expect(page.getByTestId("hub-recipient-question").query()).toBeNull();
      await expect.element(page.getByTestId("basics-who")).toHaveTextContent("You");
    });

    it("offers the helper no step down before the recipient has joined", async () => {
      hub.status = handingOver({ ...recipient, callerIsHelper: true, recipientJoined: false });
      render(SetupHub);

      await expect.element(page.getByRole("heading", { name: "Set Nocturne up for Sam." })).toBeVisible();
      expect(page.getByTestId("hub-step-down").query()).toBeNull();
      expect(page.getByTestId("hub-recipient-question").query()).toBeNull();
    });

    it("lets the helper step down once they can, and sends them to the dashboard", async () => {
      hub.status = handingOver({ ...recipient, callerIsHelper: true, canStepDown: true });
      render(SetupHub);

      const card = page.getByTestId("hub-step-down");
      await expect.element(card.getByText("Can see and log treatments")).toBeVisible();
      await expect.element(card.getByText("They can also log insulin and carbs, and change alerts.")).toBeVisible();
      await card.getByRole("button", { name: "Step down" }).click();
      await page.getByRole("alertdialog").getByRole("button", { name: "Step down" }).click();

      await vi.waitFor(() => expect(members.stepDown).toHaveBeenCalled());
      await vi.waitFor(() => expect(members.goto).toHaveBeenCalledWith("/"));
    });
  });
});
