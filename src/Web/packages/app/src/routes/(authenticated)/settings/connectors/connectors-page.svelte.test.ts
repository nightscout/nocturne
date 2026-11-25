import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { page } from "$app/state";
import ConnectorsPage from "./+page.svelte";
import {
  ConnectionDirection,
  ConnectionKind,
  ConnectionStatus,
  GoogleHealthConnectionState,
  OAuthClientIdentity,
  type Connection,
  type ConnectionsOverview,
} from "@nocturne/api-client";

const mocks = vi.hoisted(() => ({
  connections: vi.fn(),
  refreshConnections: vi.fn().mockResolvedValue(undefined),
  googleHealth: vi.fn(),
  refreshGoogleHealth: vi.fn().mockResolvedValue(undefined),
  refreshOverview: vi.fn().mockResolvedValue(undefined),
  refreshStatuses: vi.fn().mockResolvedValue(undefined),
  overview: vi.fn(),
  statuses: vi.fn(),
  revokeApp: vi.fn().mockResolvedValue({ success: true }),
  revokeToken: vi.fn().mockResolvedValue({ success: true }),
  triggerSync: vi.fn().mockResolvedValue({ success: true }),
  createToken: vi.fn().mockResolvedValue({ id: "grant-new", token: "noc_new" }),
}));

vi.mock("#lib/api/generated/googleHealths.generated.remote.js", () => ({
  getGoogleHealth: mocks.googleHealth,
}));
vi.mock("#lib/api/generated/connectorStatus.generated.remote.js", () => ({
  getStatus: () => ({
    current: mocks.statuses(),
    refresh: mocks.refreshStatuses,
  }),
}));
vi.mock("#lib/api/generated/services.generated.remote.js", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("#lib/api/generated/services.generated.remote.js")
  >()),
  getConnections: mocks.connections,
  getServicesOverview: mocks.overview,
  getConnectorCapabilities: () => ({ current: null }),
  triggerConnectorSync: mocks.triggerSync,
  deleteDataSourceData: vi.fn(),
}));
vi.mock("#lib/api/generated/connectedApps.generated.remote.js", () => ({
  revoke: mocks.revokeApp,
}));
vi.mock("#lib/api/generated/directGrants.generated.remote.js", () => ({
  create: mocks.createToken,
  revoke: mocks.revokeToken,
  keep: vi.fn(),
}));
vi.mock("#lib/api/generated/clientDevices.generated.remote.js", () => ({
  getDevices: () => ({ current: [] }),
  getCapabilityCatalog: () => ({ current: { kinds: [], capabilities: [] } }),
  rename: vi.fn(),
  revoke: vi.fn(),
}));
vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => ({ syncProgressByConnector: {} }),
}));

const minutesAgo = (minutes: number) =>
  new Date(Date.now() - minutes * 60_000).toISOString();

const xdrip: Connection = {
  id: "app:xdrip",
  kind: ConnectionKind.App,
  direction: ConnectionDirection.Sends,
  status: ConnectionStatus.Active,
  name: "xDrip+",
  identity: OAuthClientIdentity.Verified,
  lastRecordAt: minutesAgo(3),
  totalRecords: 8640,
  recordCounts: { Glucose: 8600, Boluses: 40 },
  streams: [
    {
      key: "xdrip-dexcom-g7",
      device: "xDrip-DexcomG7",
      name: "xDrip+",
      recordCounts: { Glucose: 8600, Boluses: 40 },
      totalRecords: 8640,
      lastRecordAt: minutesAgo(3),
      canDelete: false,
    },
  ],
  credentials: [
    {
      grantId: "grant-own",
      label: "Pixel 9",
      scopes: ["glucose.readwrite", "treatments.read"],
      createdAt: "2026-01-02T00:00:00Z",
      deviceCount: 2,
      isOwn: true,
    },
    {
      grantId: "grant-other",
      label: "Partner phone",
      scopes: [],
      createdAt: "2026-01-03T00:00:00Z",
      isOwn: false,
    },
  ],
};

const dexcom: Connection = {
  id: "connector:dexcom",
  kind: ConnectionKind.Connector,
  direction: ConnectionDirection.Fetches,
  status: ConnectionStatus.Error,
  name: "Dexcom",
  icon: "dexcom",
  connectorId: "dexcom",
  errorMessage: "The Dexcom password was refused.",
  lastSyncAt: minutesAgo(90),
  recordCounts: { Glucose: 12 },
  streams: [
    {
      key: "dexcom-connector",
      dataSource: "dexcom-connector",
      name: "Dexcom",
      recordCounts: { Glucose: 12 },
      totalRecords: 12,
      canDelete: true,
    },
  ],
  credentials: [],
};

const unnamedToken: Connection = {
  id: "token:1",
  kind: ConnectionKind.Token,
  direction: ConnectionDirection.NoRecords,
  status: ConnectionStatus.Quiet,
  streams: [],
  credentials: [
    {
      grantId: "grant-token",
      scopes: ["*"],
      createdAt: "2026-01-02T00:00:00Z",
      limitTo24Hours: true,
      isMigrated: true,
      isOwn: true,
    },
  ],
};

const loop: Connection = {
  id: "app:loop",
  kind: ConnectionKind.App,
  direction: ConnectionDirection.Sends,
  status: ConnectionStatus.Stopped,
  name: "Loop",
  lastRecordAt: minutesAgo(600),
  totalRecords: 20,
  streams: [],
  credentials: [],
};

const newToken: Connection = {
  id: "token:new",
  kind: ConnectionKind.Token,
  direction: ConnectionDirection.NoRecords,
  status: ConnectionStatus.Waiting,
  name: "Home Assistant",
  streams: [],
  credentials: [
    {
      grantId: "grant-new",
      scopes: ["glucose.read"],
      createdAt: minutesAgo(1),
      isOwn: true,
    },
  ],
};

function loaded(overview: ConnectionsOverview) {
  mocks.connections.mockReturnValue({
    current: { windowDays: 30, activeWithinMinutes: 15, ...overview },
    error: undefined,
    refresh: mocks.refreshConnections,
  });
}

function asManager() {
  page.data.effectivePermissions = ["tenant.settings"];
  page.data.refusedAsDemoSubject = false;
}

beforeEach(() => {
  vi.clearAllMocks();
  asManager();
  page.data.isHandedOffSession = false;
  mocks.statuses.mockReturnValue([]);
  mocks.overview.mockReturnValue({
    current: {
      uploaderApps: [{ id: "xdrip", icon: "xdrip" }],
      availableConnectors: [
        { id: "dexcom", name: "Dexcom", isConfigured: true },
        {
          id: "glooko",
          name: "Glooko",
          description: "Glooko cloud",
          documentationUrl: "https://docs.example/glooko",
        },
        { id: "googlehealth", name: "Google Health" },
      ],
      apiEndpoint: { baseUrl: "https://example.test" },
    },
    refresh: mocks.refreshOverview,
  });
  mocks.googleHealth.mockReturnValue({
    current: null,
    refresh: mocks.refreshGoogleHealth,
  });
  loaded({ connections: [xdrip, dexcom, loop, newToken, unnamedToken] });
});

describe("connections page states", () => {
  it("shows neither a list nor an empty state while the connections load", async () => {
    mocks.connections.mockReturnValue({
      current: undefined,
      error: undefined,
      refresh: mocks.refreshConnections,
    });
    render(ConnectorsPage);

    await expect
      .element(browserPage.getByRole("heading", { name: "Connectors & apps", level: 1 }))
      .toBeVisible();
    await expect
      .element(browserPage.getByText("No connections", { exact: true }))
      .not.toBeInTheDocument();
    await expect
      .element(browserPage.getByRole("button", { name: "Add a connection" }))
      .not.toBeInTheDocument();
  });

  it("reports a failed load once, with a retry", async () => {
    mocks.connections.mockReturnValue({
      current: undefined,
      error: new Error("boom"),
      refresh: mocks.refreshConnections,
    });
    render(ConnectorsPage);

    await expect.element(browserPage.getByRole("alert")).toBeVisible();
    await expect
      .element(browserPage.getByText("No connections", { exact: true }))
      .not.toBeInTheDocument();

    await browserPage.getByRole("button", { name: "Try again" }).click();
    expect(mocks.refreshConnections).toHaveBeenCalledTimes(1);
  });

  it("states that there are no connections and leads with the Nightscout move", async () => {
    page.data.effectivePermissions = ["*"];
    loaded({ connections: [] });
    render(ConnectorsPage);

    await expect
      .element(browserPage.getByText("No connections", { exact: true }))
      .toBeVisible();
    await expect
      .element(browserPage.getByRole("button", { name: "Add a connection" }))
      .toBeVisible();
    await expect
      .element(browserPage.getByRole("link", { name: /Move from Nightscout/ }))
      .toBeVisible();
  });
});

describe("connections list", () => {
  it("lists active and error connections under a real heading", async () => {
    render(ConnectorsPage);

    await expect
      .element(browserPage.getByRole("heading", { name: "Connections", level: 2 }))
      .toBeVisible();

    const xdripRow = browserPage.getByRole("button", { name: /xDrip\+/ });
    await expect.element(xdripRow).toBeVisible();
    await expect.element(xdripRow).toHaveTextContent("Sends to Nocturne");
    await expect.element(xdripRow).toHaveTextContent("Active");
    await expect
      .element(xdripRow)
      .toHaveTextContent("8,640 records in the last 30 days");
    await expect.element(xdripRow).toHaveTextContent("Last record");

    const dexcomRow = browserPage.getByRole("button", { name: /Dexcom/ });
    await expect.element(dexcomRow).toHaveTextContent("Nocturne fetches");
    await expect.element(dexcomRow).toHaveTextContent("Error");
    await expect.element(dexcomRow).toHaveTextContent("Last sync");
    await expect
      .element(dexcomRow)
      .toHaveTextContent("The Dexcom password was refused.");
  });

  it("keeps a stopped and a waiting connection in the main list, with their status", async () => {
    render(ConnectorsPage);

    const stopped = browserPage.getByRole("button", { name: /Loop/ });
    await expect.element(stopped).toBeVisible();
    await expect.element(stopped).toHaveTextContent("Stopped");
    await expect.element(stopped).toHaveTextContent("Last record");

    const waiting = browserPage.getByRole("button", { name: /Home Assistant/ });
    await expect.element(waiting).toBeVisible();
    await expect.element(waiting).toHaveTextContent("Waiting for first use");
  });

  it("keeps connections that are not in use behind a disclosure that counts them", async () => {
    render(ConnectorsPage);

    const disclosure = browserPage.getByRole("button", { name: "Not in use (1)" });
    await expect.element(disclosure).toBeVisible();
    await expect
      .element(browserPage.getByRole("button", { name: /API token/ }))
      .not.toBeInTheDocument();

    await disclosure.click();

    const tokenRow = browserPage.getByRole("button", { name: /API token/ });
    await expect.element(tokenRow).toBeVisible();
    await expect
      .element(tokenRow)
      .toHaveTextContent("No records in the last 30 days");
    await expect.element(tokenRow).toHaveTextContent("Last used");
  });

  it("lists other sources only when there are some, collapsed", async () => {
    loaded({
      connections: [xdrip],
      otherSources: [
        {
          key: "mystery",
          name: "Mystery uploader",
          totalRecords: 12,
          canDelete: true,
        },
        {
          key: "shared",
          name: "Shared name",
          totalRecords: 7,
          canDelete: false,
        },
      ],
    });
    render(ConnectorsPage);

    await expect
      .element(browserPage.getByRole("heading", { name: "Other sources", level: 2 }))
      .toBeVisible();
    const disclosure = browserPage.getByRole("button", { name: "2 sources" });
    await expect.element(disclosure).toBeVisible();
    await expect
      .element(
        browserPage.getByText(
          "Records in the last 30 days that no connection wrote."
        )
      )
      .toBeVisible();
    await expect
      .element(browserPage.getByRole("button", { name: /Mystery uploader/ }))
      .not.toBeInTheDocument();

    await disclosure.click();
    await expect
      .element(
        browserPage.getByText("A connection also sends records under this name.")
      )
      .toBeVisible();
    await expect.element(browserPage.getByText("Shared name")).toBeVisible();
    expect(browserPage.getByRole("button", { name: /Shared name/ }).elements()).toHaveLength(0);
    await browserPage.getByRole("button", { name: /Mystery uploader/ }).click();
    await expect
      .element(browserPage.getByRole("button", { name: "Delete data..." }))
      .toBeVisible();
  });

  it("omits the other sources card when there are none", async () => {
    render(ConnectorsPage);

    await expect
      .element(browserPage.getByRole("button", { name: /xDrip\+/ }))
      .toBeVisible();
    await expect
      .element(browserPage.getByText(/Other sources/))
      .not.toBeInTheDocument();
  });
});

describe("connection detail", () => {
  it("tells with the server's threshold why a stopped connection has no recent record", async () => {
    loaded({ connections: [loop], activeWithinMinutes: 20 });
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: /Loop/ }).click();

    await expect
      .element(browserPage.getByRole("dialog").getByText(/last 20 minutes/))
      .toHaveTextContent("It is possible that it stopped");
  });

  it("tells that a quiet connection that sends treatments only is normal", async () => {
    loaded({
      connections: [
        { ...loop, id: "app:pump", name: "Pump app", status: ConnectionStatus.Quiet },
      ],
    });
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: "Not in use (1)" }).click();
    await browserPage.getByRole("button", { name: /Pump app/ }).click();

    await expect
      .element(browserPage.getByRole("dialog").getByText(/last 15 minutes/))
      .toHaveTextContent("This is normal");
  });

  it("shows streams and credentials, and revokes only the viewer's own grant", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: /xDrip\+/ }).click();

    const dialog = browserPage.getByRole("dialog");
    await expect
      .element(dialog.getByRole("heading", { name: "Access" }))
      .toBeVisible();
    await expect
      .element(dialog.getByRole("heading", { name: "Records in the last 30 days" }))
      .toBeVisible();
    await expect.element(dialog.getByText("8,600").first()).toBeVisible();
    expect(dialog.getByRole("heading", { name: "Streams" }).elements()).toHaveLength(0);
    await expect.element(dialog.getByText("xDrip-DexcomG7")).not.toBeVisible();
    await dialog.getByRole("button", { name: "Records by device name" }).click();
    await expect.element(dialog.getByText("xDrip-DexcomG7")).toBeVisible();
    await expect.element(dialog.getByText("Pixel 9")).toBeVisible();
    await expect.element(dialog.getByText("Verified", { exact: true })).toBeVisible();
    await expect.element(dialog.getByText("Partner phone")).toBeVisible();
    await expect
      .element(dialog.getByText("Belongs to another member"))
      .toBeVisible();
    expect(dialog.getByRole("button", { name: "Revoke" }).elements()).toHaveLength(1);
    expect(dialog.getByRole("button", { name: /permission/ }).elements()).toHaveLength(1);

    await dialog.getByRole("button", { name: "2 permissions" }).click();
    await expect
      .element(dialog.getByText("View, record and remove glucose readings"))
      .toBeVisible();

    await dialog.getByRole("button", { name: "Revoke" }).click();
    const confirm = browserPage.getByRole("alertdialog");
    await expect.element(confirm).toHaveTextContent("2 paired devices");
    await confirm.getByRole("button", { name: "Revoke" }).click();

    await expect.poll(() => mocks.revokeApp.mock.calls).toEqual([["grant-own"]]);
    expect(mocks.revokeToken).not.toHaveBeenCalled();
    // The command invalidates the connections itself.
    expect(mocks.refreshConnections).not.toHaveBeenCalled();
  });

  it("reads the wildcard scope as full access and revokes a token as a direct grant", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: "Not in use (1)" }).click();
    await browserPage.getByRole("button", { name: /API token/ }).click();

    const dialog = browserPage.getByRole("dialog");
    await expect.element(dialog.getByText("Full access")).toBeVisible();
    expect(
      dialog.getByText(/No records in the last 30 days/).elements()
    ).toHaveLength(1);
    await expect.element(dialog.getByText("24-hour limit")).toBeVisible();
    await expect
      .element(dialog.getByText("Legacy: rotate to per-device key"))
      .toBeVisible();

    await dialog.getByRole("button", { name: "Revoke" }).click();
    await browserPage
      .getByRole("alertdialog")
      .getByRole("button", { name: "Revoke" })
      .click();

    await expect
      .poll(() => mocks.revokeToken.mock.calls)
      .toEqual([["grant-token"]]);
    expect(mocks.revokeApp).not.toHaveBeenCalled();
  });

  it("offers no delete on the stream of an app, whoever manages the site", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: /xDrip\+/ }).click();

    const dialog = browserPage.getByRole("dialog");
    await dialog.getByRole("button", { name: "Records by device name" }).click();
    await expect.element(dialog.getByText("xDrip-DexcomG7")).toBeVisible();
    expect(dialog.getByRole("button", { name: /Delete data/ }).elements()).toHaveLength(0);
  });

  it("offers no delete on the stream of a token", async () => {
    loaded({
      connections: [
        {
          ...newToken,
          direction: ConnectionDirection.Sends,
          status: ConnectionStatus.Active,
          recordCounts: { Glucose: 3 },
          streams: [
            {
              key: "ha",
              device: "home-assistant",
              name: "Home Assistant",
              recordCounts: { Glucose: 3 },
              totalRecords: 3,
              canDelete: false,
            },
          ],
        },
      ],
    });
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: /Home Assistant/ }).click();

    const dialog = browserPage.getByRole("dialog");
    await dialog.getByRole("button", { name: "Records by device name" }).click();
    await expect.element(dialog.getByText("home-assistant")).toBeVisible();
    expect(dialog.getByRole("button", { name: /Delete data from/ }).elements()).toHaveLength(0);
  });

  it("offers delete on a connector stream, and returns to the connection on cancel", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: /Dexcom/ }).click();
    await browserPage
      .getByRole("dialog")
      .getByRole("button", { name: "Records by device name" })
      .click();
    await browserPage
      .getByRole("button", { name: "Delete data from Dexcom" })
      .click();
    await expect
      .element(browserPage.getByRole("button", { name: "Delete data..." }))
      .toBeVisible();

    await browserPage.getByRole("button", { name: "Cancel" }).click();

    await expect
      .element(browserPage.getByRole("dialog").getByText("Nocturne fetches"))
      .toBeVisible();
  });

  it("shows only the label and the owner note for another member's credential", async () => {
    loaded({
      connections: [
        {
          ...xdrip,
          lastRecordAt: undefined,
          credentials: [
            {
              grantId: "grant-other",
              label: "Partner phone",
              scopes: [],
              createdAt: "2026-01-03T00:00:00Z",
              isOwn: false,
            },
          ],
        },
      ],
    });
    render(ConnectorsPage);

    const row = browserPage.getByRole("button", { name: /xDrip\+/ });
    await expect.element(row).toBeVisible();
    await expect.element(row).not.toHaveTextContent("Last used");
    await row.click();

    const dialog = browserPage.getByRole("dialog");
    await expect.element(dialog.getByText("Partner phone")).toBeVisible();
    await expect
      .element(dialog.getByText("Belongs to another member"))
      .toBeVisible();
    await expect.element(dialog).not.toHaveTextContent("Last used");
    await expect.element(dialog).not.toHaveTextContent("Never");
    expect(dialog.getByRole("button", { name: /permission/ }).elements()).toHaveLength(0);
    expect(dialog.getByRole("button", { name: "Revoke" }).elements()).toHaveLength(0);
  });

  it("lists an imported connector as imported, with no sync", async () => {
    const oura: Connection = {
      id: "connector:oura",
      kind: ConnectionKind.Connector,
      direction: ConnectionDirection.Imports,
      status: ConnectionStatus.Imported,
      name: "Oura",
      icon: "oura",
      connectorId: "oura",
      lastRecordAt: minutesAgo(6000),
      recordCounts: { HeartRate: 500 },
      streams: [],
      credentials: [],
    };
    loaded({ connections: [xdrip, oura] });
    render(ConnectorsPage);

    const ouraRow = browserPage.getByRole("button", { name: /Oura/ });
    await expect.element(ouraRow).toHaveTextContent("Imported from a file");
    await expect.element(ouraRow).toHaveTextContent("Imported");
    await expect.element(ouraRow).not.toHaveTextContent("Off");
    await expect.element(ouraRow).not.toHaveTextContent("Last sync");

    await ouraRow.click();
    const dialog = browserPage.getByRole("dialog");
    await expect.element(dialog.getByRole("link", { name: "Configure" })).toBeVisible();
    expect(dialog.getByRole("button", { name: "Sync now" }).elements()).toHaveLength(0);
  });

  it("offers a connector its configuration page and its sync", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: /Dexcom/ }).click();

    const dialog = browserPage.getByRole("dialog");
    await expect
      .element(dialog.getByRole("link", { name: "Configure" }))
      .toHaveAttribute("href", expect.stringContaining("settings/connectors/dexcom"));

    await dialog.getByRole("button", { name: "Sync now" }).click();
    await expect
      .poll(() => mocks.triggerSync.mock.calls)
      .toEqual([[{ id: "dexcom", request: {} }]]);
  });
});

describe("member who cannot manage connectors", () => {
  beforeEach(() => {
    page.data.effectivePermissions = ["glucose.read"];
  });

  it("keeps the rights over their own credentials and loses the connector actions", async () => {
    render(ConnectorsPage);

    await expect
      .element(browserPage.getByRole("button", { name: "Add a connection" }))
      .toBeVisible();
    await expect
      .element(browserPage.getByRole("button", { name: "Sync all connectors" }))
      .not.toBeInTheDocument();

    await browserPage.getByRole("button", { name: /xDrip\+/ }).click();
    const dialog = browserPage.getByRole("dialog");
    await expect.element(dialog.getByText("Pixel 9")).toBeVisible();
    expect(dialog.getByRole("button", { name: "Revoke" }).elements()).toHaveLength(1);
    expect(dialog.getByRole("button", { name: /Delete data/ }).elements()).toHaveLength(0);
  });

  it("offers a connector no configuration and no sync", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: /Dexcom/ }).click();

    const dialog = browserPage.getByRole("dialog");
    await expect.element(dialog.getByText("Nocturne fetches")).toBeVisible();
    expect(dialog.getByRole("link", { name: "Configure" }).elements()).toHaveLength(0);
    expect(dialog.getByRole("button", { name: "Sync now" }).elements()).toHaveLength(0);
  });

  it("adds an app or a token, and is not offered a cloud account", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: "Add a connection" }).click();

    const dialog = browserPage.getByRole("dialog");
    await expect
      .element(dialog.getByRole("button", { name: /xDrip\+/ }))
      .toBeVisible();
    await expect
      .element(dialog.getByRole("button", { name: /Create API token/ }))
      .toBeVisible();
    await expect
      .element(dialog.getByRole("link", { name: /Enter a device code/ }))
      .toBeVisible();
    expect(dialog.getByRole("button", { name: /A cloud account/ }).elements()).toHaveLength(0);
    expect(dialog.getByRole("button", { name: "Back" }).elements()).toHaveLength(0);
  });
});

describe("add a connection", () => {
  it("lists uploader apps and the manual setup in the app branch", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: "Add a connection" }).click();

    const dialog = browserPage.getByRole("dialog");
    await dialog
      .getByRole("button", { name: /An app on your phone or computer/ })
      .click();

    await expect
      .element(dialog.getByRole("button", { name: /xDrip\+/ }))
      .toBeVisible();
    await expect
      .element(dialog.getByRole("heading", { name: "Set up manually" }))
      .toBeVisible();
    await expect
      .element(dialog.getByRole("button", { name: /Create API token/ }))
      .toBeVisible();
    await expect
      .element(dialog.getByRole("button", { name: "Copy base URL" }))
      .toBeVisible();

    await dialog.getByRole("searchbox", { name: "Search apps" }).fill("zzz");
    await expect.element(dialog.getByText("No apps match.")).toBeVisible();
  });

  it("lists only connectors with no configuration in the cloud branch", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: "Add a connection" }).click();

    const dialog = browserPage.getByRole("dialog");
    await dialog.getByRole("button", { name: /A cloud account/ }).click();

    await expect
      .element(dialog.getByRole("link", { name: /Glooko cloud/ }))
      .toHaveAttribute("href", expect.stringContaining("settings/connectors/glooko"));
    await expect
      .element(dialog.getByRole("link", { name: "Glooko documentation" }))
      .toHaveAttribute("href", "https://docs.example/glooko");
    await expect
      .element(dialog.getByRole("link", { name: /Dexcom/ }))
      .not.toBeInTheDocument();
  });

  it("moves focus to the row of a token it created", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: "Add a connection" }).click();
    await browserPage
      .getByRole("dialog")
      .getByRole("button", { name: /An app on your phone or computer/ })
      .click();
    await browserPage
      .getByRole("dialog")
      .getByRole("button", { name: /Create API token/ })
      .click();

    const dialog = browserPage.getByRole("dialog");
    await dialog.getByRole("textbox", { name: "Label" }).fill("Home Assistant");
    await dialog.getByRole("button", { name: "Create token" }).click();
    await expect
      .element(dialog.getByRole("textbox", { name: "Base URL" }))
      .toHaveValue("https://example.test");
    await dialog.getByRole("button", { name: "Done" }).click();

    await expect
      .element(browserPage.getByRole("button", { name: /Home Assistant/ }))
      .toHaveFocus();
  });

  it("shows an unavailable Google Health as a reason, not a link", async () => {
    mocks.googleHealth.mockReturnValue({
      current: { available: false, configured: false, connected: false },
      loading: false,
      error: undefined,
      refresh: mocks.refreshGoogleHealth,
    });
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: "Add a connection" }).click();

    const dialog = browserPage.getByRole("dialog");
    await dialog.getByRole("button", { name: /A cloud account/ }).click();

    await expect
      .element(dialog.getByText("Not enabled on this Nocturne server"))
      .toBeVisible();
    expect(dialog.getByRole("link", { name: /Google Health/ }).elements()).toHaveLength(0);
  });

  it.each([
    { loading: true, error: undefined, label: "Loading..." },
    {
      loading: false,
      error: new Error("status failed"),
      label: "Status unavailable",
    },
    { loading: false, error: undefined, label: "Not connected" },
  ])(
    "offers Google Health with its status as $label",
    async ({ loading, error, label }) => {
      mocks.googleHealth.mockReturnValue({
        current: undefined,
        loading,
        error,
        refresh: mocks.refreshGoogleHealth,
      });
      render(ConnectorsPage);
      await browserPage.getByRole("button", { name: "Add a connection" }).click();

      const dialog = browserPage.getByRole("dialog");
      await dialog.getByRole("button", { name: /A cloud account/ }).click();

      await expect
        .element(
          dialog
            .getByRole("link", { name: /Google Health/ })
            .getByText(label, { exact: true })
        )
        .toBeVisible();
    }
  );
});

describe("Google Health status", () => {
  it("lists a configured Google Health import with the connections", async () => {
    mocks.googleHealth.mockReturnValue({
      current: {
        available: true,
        connectionState: GoogleHealthConnectionState.Active,
        configured: true,
        connected: true,
      },
      loading: false,
      error: undefined,
      refresh: mocks.refreshGoogleHealth,
    });
    render(ConnectorsPage);

    await expect
      .element(browserPage.getByRole("link", { name: /Google Health/ }))
      .toHaveTextContent("Waiting for the first successful sync");
  });

  it("keeps a disconnected Google Health import listed, with its next step", async () => {
    mocks.googleHealth.mockReturnValue({
      current: {
        available: true,
        connectionState: GoogleHealthConnectionState.Disconnected,
        configured: true,
        connected: false,
      },
      loading: false,
      error: undefined,
      refresh: mocks.refreshGoogleHealth,
    });
    render(ConnectorsPage);

    await expect
      .element(browserPage.getByRole("link", { name: /Google Health/ }))
      .toHaveTextContent("Reconnect to resume importing");
  });

  it.each([
    { permissions: ["glucose.read"], demo: false, allowed: false },
    { permissions: ["tenant.settings"], demo: true, allowed: false },
    { permissions: ["*"], demo: true, allowed: false },
    { permissions: ["tenant.settings"], demo: false, allowed: true },
    { permissions: ["*"], demo: false, allowed: true },
  ])(
    "gates status query and refresh for $permissions (demo=$demo)",
    async ({ permissions, demo, allowed }) => {
      page.data.effectivePermissions = permissions;
      page.data.refusedAsDemoSubject = demo;
      render(ConnectorsPage);

      await expect
        .element(browserPage.getByRole("button", { name: /xDrip\+/ }))
        .toBeVisible();
      expect(mocks.googleHealth).toHaveBeenCalledTimes(allowed ? 1 : 0);
      await browserPage
        .getByRole("button", { name: "Refresh", exact: true })
        .click();
      expect(mocks.refreshGoogleHealth).toHaveBeenCalledTimes(allowed ? 1 : 0);
      expect(mocks.refreshConnections).toHaveBeenCalledTimes(1);
    }
  );
});

describe("caretaker, who sets up data sources but cannot manage settings", () => {
  beforeEach(() => {
    page.data.effectivePermissions = ["glucose.read", "treatments.readwrite", "connectors.readwrite"];
  });

  it("syncs every connector and opens a connector's configuration and sync", async () => {
    render(ConnectorsPage);

    await expect
      .element(browserPage.getByRole("button", { name: "Sync all connectors" }))
      .toBeVisible();
    await browserPage.getByRole("button", { name: /Dexcom/ }).click();
    const dialog = browserPage.getByRole("dialog");
    await expect.element(dialog.getByRole("link", { name: "Configure" })).toBeVisible();
    await expect.element(dialog.getByRole("button", { name: "Sync now" })).toBeVisible();
  });

  it("is offered a cloud account but not Google Health, which needs the site's settings", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: "Add a connection" }).click();

    const dialog = browserPage.getByRole("dialog");
    await expect.element(dialog.getByRole("button", { name: /A cloud account/ })).toBeVisible();
    expect(mocks.googleHealth).toHaveBeenCalledTimes(0);
  });
});

/**
 * A session opened from a sign-in link someone else issued is refused the connections list, a
 * token and a device approval, and nothing else on this page. Only those give way to the sign-in.
 */
describe("a session opened from a sign-in link", () => {
  beforeEach(() => {
    page.data.isHandedOffSession = true;
  });

  it("offers the sign-in in place of the list, and does not ask for it", async () => {
    render(ConnectorsPage);

    await expect
      .element(browserPage.getByText("Sign in again to see your connections", { exact: false }))
      .toBeVisible();
    await expect.element(browserPage.getByText("xDrip+")).not.toBeInTheDocument();
    expect(mocks.connections).not.toHaveBeenCalled();
  });

  it("places the Nightscout move below the sign-in, since the connections were not read", async () => {
    page.data.effectivePermissions = ["*"];
    render(ConnectorsPage);

    const move = browserPage.getByRole("link", { name: /Move from Nightscout/ });
    await expect.element(move).toBeVisible();
    expect(move.elements()).toHaveLength(1);
    const gate = browserPage.getByTestId("sign-in-gate").element();
    expect(gate.compareDocumentPosition(move.element()) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("still adds a cloud account", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: "Add a connection" }).click();

    const dialog = browserPage.getByRole("dialog");
    await dialog.getByRole("button", { name: /A cloud account/ }).click();

    await expect
      .element(dialog.getByRole("link", { name: /Glooko cloud/ }))
      .toHaveAttribute("href", expect.stringContaining("settings/connectors/glooko"));
  });

  it("offers the sign-in in place of the apps and the token, and keeps the base URL", async () => {
    render(ConnectorsPage);
    await browserPage.getByRole("button", { name: "Add a connection" }).click();

    const dialog = browserPage.getByRole("dialog");
    await dialog
      .getByRole("button", { name: /An app on your phone or computer/ })
      .click();

    await expect
      .element(dialog.getByText("Sign in again to connect an app or create an API token."))
      .toBeVisible();
    await expect.element(dialog.getByRole("button", { name: /xDrip\+/ })).not.toBeInTheDocument();
    await expect
      .element(dialog.getByRole("button", { name: /Create API token/ }))
      .not.toBeInTheDocument();
    await expect
      .element(dialog.getByRole("link", { name: /Enter a device code/ }))
      .not.toBeInTheDocument();
    await expect.element(dialog.getByRole("button", { name: "Copy base URL" })).toBeVisible();
  });
});
