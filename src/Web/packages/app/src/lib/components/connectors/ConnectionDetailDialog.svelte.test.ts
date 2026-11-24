import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import {
  ConnectionDirection,
  ConnectionKind,
  ConnectionStatus,
  ConnectorTimeZoneSource,
  type Connection,
} from "@nocturne/api-client";

let deleteArgs: unknown[];
let deleteImpl: () => Promise<unknown>;
const keepDeviceCredential = vi.hoisted(() => vi.fn(() => Promise.resolve({ success: true })));

vi.mock("#lib/api/generated/services.generated.remote.js", () => ({
  deleteConnectionRecords: (args: unknown) => {
    deleteArgs.push(args);
    return deleteImpl();
  },
}));
vi.mock("#lib/api/generated/connectedApps.generated.remote.js", () => ({
  revoke: vi.fn(),
}));
vi.mock("#lib/api/generated/directGrants.generated.remote.js", () => ({
  revoke: vi.fn(),
  keep: keepDeviceCredential,
}));

const toastSuccess = vi.hoisted(() => vi.fn());
vi.mock("svelte-sonner", () => ({ toast: { success: toastSuccess } }));

import ConnectionDetailDialog from "./ConnectionDetailDialog.svelte";

const ownGrant = "11111111-1111-1111-1111-111111111111";
const otherGrant = "22222222-2222-2222-2222-222222222222";

const token: Connection = {
  id: `token:${ownGrant}`,
  kind: ConnectionKind.Token,
  direction: ConnectionDirection.Sends,
  status: ConnectionStatus.Active,
  name: "Phone uploader",
  recordCounts: { Glucose: 2, Boluses: 1 },
  credentials: [{ grantId: ownGrant, label: "Phone uploader", isOwn: true }],
  streams: [],
  canDeleteRecords: true,
};

/** What the viewer may do: manage the site's settings, only set up data sources, or neither. */
const SETTINGS = { canManage: true, canSetUp: true };
const CARETAKER = { canManage: false, canSetUp: true };
const VIEWER = { canManage: false, canSetUp: false };

function renderDialog(connection: Connection, access: { canManage: boolean; canSetUp: boolean }) {
  return render(ConnectionDetailDialog, {
    props: {
      open: true,
      connection,
      windowDays: 30,
      activeWithinMinutes: 15,
      ...access,
      onQuickSync: () => {},
      onDeleteStream: () => {},
    },
  });
}

describe("ConnectionDetailDialog", () => {
  beforeEach(() => {
    deleteArgs = [];
    deleteImpl = () => Promise.resolve({ success: true, totalDeleted: 3 });
    toastSuccess.mockClear();
  });

  it("deletes the records of the viewer's own credentials after the typed confirmation", async () => {
    renderDialog(token, SETTINGS);

    await page.getByRole("button", { name: "Delete data..." }).click();
    const confirm = page.getByRole("button", { name: "Delete all data" });
    await expect.element(confirm).toBeDisabled();
    await page.getByRole("textbox").fill("DELETE");
    await confirm.click();

    await expect
      .poll(() => deleteArgs)
      .toEqual([{ grantIds: [ownGrant], allowReimport: false }]);
    await expect
      .poll(() => toastSuccess.mock.calls)
      .toEqual([["Deleted 3 records from Phone uploader"]]);
  });

  it("allows re-import when the option is checked", async () => {
    renderDialog(token, SETTINGS);

    await page.getByRole("button", { name: "Delete data..." }).click();
    await page.getByRole("checkbox", { name: /Allow re-import/ }).click();
    await page.getByRole("textbox").fill("DELETE");
    await page.getByRole("button", { name: "Delete and allow re-import" }).click();

    await expect
      .poll(() => deleteArgs)
      .toEqual([{ grantIds: [ownGrant], allowReimport: true }]);
  });

  it("offers the delete for an own connection with no records in the window", async () => {
    renderDialog({
      ...token,
      direction: ConnectionDirection.NoRecords,
      recordCounts: {},
    }, SETTINGS);

    await expect
      .element(page.getByRole("button", { name: "Delete data..." }))
      .toBeVisible();
  });

  it("says a refused delete is about the credential, not about missing records", async () => {
    deleteImpl = async () => error(404, "Not found");
    renderDialog(token, SETTINGS);

    await page.getByRole("button", { name: "Delete data..." }).click();
    await page.getByRole("textbox").fill("DELETE");
    await page.getByRole("button", { name: "Delete all data" }).click();

    await expect
      .element(page.getByText("You cannot delete this data"))
      .toBeInTheDocument();
    await expect
      .element(
        page.getByText("A credential of this connection is not yours, or it was removed.")
      )
      .toBeInTheDocument();
    expect(toastSuccess).not.toHaveBeenCalled();
  });

  it("offers no delete when the server finds a credential of the connection that is not the viewer's, listed or not", async () => {
    renderDialog({ ...token, canDeleteRecords: false }, SETTINGS);

    await expect.element(page.getByText("Phone uploader").first()).toBeVisible();
    expect(
      page.getByRole("button", { name: "Delete data..." }).elements()
    ).toHaveLength(0);
  });

  it("offers no delete for a connection with another member's credential", async () => {
    renderDialog({
      ...token,
      canDeleteRecords: false,
      credentials: [
        ...token.credentials!,
        { grantId: otherGrant, label: "Theirs", isOwn: false },
      ],
    }, SETTINGS);

    await expect.element(page.getByText("Belongs to another member")).toBeVisible();
    expect(
      page.getByRole("button", { name: "Delete data..." }).elements()
    ).toHaveLength(0);
  });

  it("offers no delete to a viewer who cannot manage connections", async () => {
    renderDialog(token, VIEWER);

    await expect.element(page.getByText("Phone uploader").first()).toBeVisible();
    expect(
      page.getByRole("button", { name: "Delete data..." }).elements()
    ).toHaveLength(0);
  });

  it("offers no connection delete for a connector, whose records go by data source", async () => {
    renderDialog({
      id: "connector:dexcom",
      kind: ConnectionKind.Connector,
      direction: ConnectionDirection.Fetches,
      status: ConnectionStatus.Active,
      name: "Dexcom",
      connectorId: "dexcom",
      recordCounts: { Glucose: 10 },
      credentials: [],
      streams: [],
    }, SETTINGS);

    await expect.element(page.getByText("Dexcom").first()).toBeVisible();
    expect(
      page.getByRole("button", { name: "Delete data..." }).elements()
    ).toHaveLength(0);
  });

  const dexcom: Connection = {
    id: "connector:dexcom",
    kind: ConnectionKind.Connector,
    direction: ConnectionDirection.Fetches,
    status: ConnectionStatus.Active,
    name: "Dexcom",
    connectorId: "dexcom",
    recordCounts: { Glucose: 10 },
    credentials: [],
    streams: [],
  };

  it("offers a caretaker and a settings holder configure and sync on a connector", async () => {
    for (const access of [CARETAKER, SETTINGS]) {
      const dialog = renderDialog(dexcom, access);

      await expect.element(page.getByRole("link", { name: "Configure" })).toBeVisible();
      await expect.element(page.getByRole("button", { name: "Sync now" })).toBeVisible();
      dialog.unmount();
    }
  });

  it("offers a viewer neither configure nor sync on a connector", async () => {
    renderDialog(dexcom, VIEWER);

    await expect.element(page.getByText("Dexcom").first()).toBeVisible();
    expect(page.getByRole("link", { name: "Configure" }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Sync now" }).elements()).toHaveLength(0);
  });

  it("names the time zone a connector read its source in, and where it came from", async () => {
    renderDialog(
      { ...dexcom, timeZone: { id: "America/Denver", source: ConnectorTimeZoneSource.TenantZone } },
      SETTINGS
    );

    await expect.element(page.getByText("America/Denver")).toBeVisible();
    await expect.element(page.getByText("(from your Timezone setting)")).toBeVisible();
  });

  it("says when the time zone came from the time zone history", async () => {
    renderDialog(
      { ...dexcom, timeZone: { id: "America/Los_Angeles", source: ConnectorTimeZoneSource.TenantTimeline } },
      SETTINGS
    );

    await expect.element(page.getByText("America/Los_Angeles")).toBeVisible();
    await expect.element(page.getByText("(from your Timezone history)")).toBeVisible();
  });

  it("says when a connector had no time zone to read its source in", async () => {
    renderDialog(
      { ...dexcom, timeZone: { id: "UTC", source: ConnectorTimeZoneSource.Utc } },
      SETTINGS
    );

    await expect.element(page.getByText("(no time zone is set)")).toBeVisible();
  });

  it("shows no time zone for a connector that records none", async () => {
    renderDialog(dexcom, SETTINGS);

    await expect.element(page.getByText("Dexcom").first()).toBeVisible();
    expect(page.getByText("Time zone").elements()).toHaveLength(0);
  });

  it("tells a caretaker why the data delete is not theirs", async () => {
    renderDialog(token, CARETAKER);

    await expect
      .element(page.getByTestId("connection-delete-reason"))
      .toHaveTextContent("Only members who can manage this site's settings can delete synced data.");
    expect(page.getByRole("button", { name: "Delete data..." }).elements()).toHaveLength(0);
  });

  it("gives a settings holder the data delete and a viewer no reason", async () => {
    const settings = renderDialog(token, SETTINGS);
    await expect.element(page.getByRole("button", { name: "Delete data..." })).toBeVisible();
    expect(page.getByTestId("connection-delete-reason").elements()).toHaveLength(0);
    settings.unmount();

    renderDialog(token, VIEWER);
    await expect.element(page.getByText("Phone uploader").first()).toBeVisible();
    expect(page.getByTestId("connection-delete-reason").elements()).toHaveLength(0);
  });

  describe("a device credential a member set up", () => {
    const campGrant = "33333333-3333-3333-3333-333333333333";

    function camp(credential: Partial<NonNullable<Connection["credentials"]>[number]>): Connection {
      return {
        ...token,
        id: `token:${campGrant}`,
        name: "Camp uploader",
        canDeleteRecords: false,
        credentials: [
          {
            grantId: campGrant,
            label: "Camp uploader",
            isOwn: false,
            stopsAt: "2026-12-02T00:00:00Z",
            setUpByName: "Sam",
            ...credential,
          },
        ],
      };
    }

    beforeEach(() => keepDeviceCredential.mockClear());

    it("says when it stops and who set it up", async () => {
      renderDialog(camp({ hasStopped: false }), SETTINGS);

      await expect
        .element(page.getByTestId("credential-stop"))
        .toHaveTextContent(/^Stops on .+\. Sam set it up\.$/);
    });

    it("says when it stopped, in the warning tone", async () => {
      renderDialog(camp({ hasStopped: true }), SETTINGS);

      const line = page.getByTestId("credential-stop");
      await expect.element(line).toHaveTextContent(/^Stopped on .+\. Sam set it up\.$/);
      await expect.element(line).toHaveClass(/text-warning/);
    });

    it("keeps it when the API says the viewer may", async () => {
      renderDialog(camp({ canKeep: true }), SETTINGS);

      await page.getByRole("button", { name: "Keep this connection" }).click();

      await expect.poll(() => keepDeviceCredential.mock.calls).toEqual([[campGrant]]);
      expect(toastSuccess).toHaveBeenCalledWith("Connection kept.");
    });

    it("shows no keep control when the API says the viewer may not", async () => {
      renderDialog(camp({ canKeep: false }), SETTINGS);

      await expect.element(page.getByTestId("credential-stop")).toBeVisible();
      await expect
        .element(page.getByRole("button", { name: "Keep this connection" }))
        .not.toBeInTheDocument();
    });
  });
});
