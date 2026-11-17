import { afterEach, describe, expect, it, vi } from "vitest";

const { renderPlotSnapshot } = vi.hoisted(() => ({ renderPlotSnapshot: vi.fn(() => "<svg></svg>") }));
vi.mock("#lib/server/plot-snapshot.js", () => ({ renderPlotSnapshot, hasPlotSize: () => true }));

const { load } = await import("./+page.server");
const { load: layoutLoad } = await import("../+layout.server");

type LoadEvent = Parameters<typeof load>[0];

const cookies = { get: () => undefined };
const getUISettings = vi.fn(async () => ({ features: { display: { focusHours: 6 } } }));
const getStatus = vi.fn(async () => ({ available: true }));
const settingsClients = { uiSettings: { getUISettings }, predictions: { getStatus } };

function runLoad(
  getDashboardChartData: ReturnType<typeof vi.fn>,
  isAuthenticated = true,
  isDataRequest = false,
  viewer: { isShareHost?: boolean; isGuestSession?: boolean } = {}
) {
  vi.stubEnv("BASE_DOMAIN", "nocturne.run");
  return load({
    locals: {
      apiClient: { chartData: { getDashboardChartData }, ...settingsClients },
      isAuthenticated,
      isShareHost: false,
      ...viewer,
    },
    request: new Request("https://sleepy.nocturne.run/", { headers: { host: "sleepy.nocturne.run" } }),
    parent: async () => ({ tenantless: false, baseDomain: "nocturne.run", dashboardSlugs: [] }),
    cookies,
    isDataRequest,
  } as unknown as LoadEvent) as Promise<{
    plotSnapshot: string | null;
    initialUiSettings: unknown;
    initialPredictionsAvailable: boolean | null;
    streamed: { historicalChartData: Promise<unknown> };
  }>;
}

describe("dashboard page load", () => {
  it("draws the snapshot with the layers the root layout hands the client", async () => {
    vi.stubEnv("BASE_DOMAIN", "nocturne.run");
    const memberCookies = {
      get: (name: string) =>
        name === "nocturne-prefs" ? encodeURIComponent(JSON.stringify({ colorTheme: "mono" })) : undefined,
    };
    const locals = {
      apiClient: {
        chartData: { getDashboardChartData: vi.fn().mockResolvedValue({}) },
        status: { getStatus: async () => ({ tenantSlug: null }) },
        myPermissions: { getMyPermissions: async () => ({ scopes: [], limitTo24Hours: false }) },
        ...settingsClients,
      },
      user: { preferences: { glucoseUnits: "mmol" }, defaultGlucoseUnits: "mg/dl" },
      isAuthenticated: true,
      isShareHost: false,
      isGuestSession: false,
    };
    const request = new Request("https://sleepy.nocturne.run/", { headers: { host: "sleepy.nocturne.run" } });

    await load({
      locals,
      request,
      parent: async () => ({}),
      cookies: memberCookies,
      isDataRequest: false,
    } as unknown as LoadEvent);
    const layout = (await layoutLoad({
      locals,
      request,
      cookies: memberCookies,
      depends: vi.fn(),
    } as unknown as Parameters<typeof layoutLoad>[0])) as { displayPreferences: unknown[] };

    const [{ preferenceLayers }] = renderPlotSnapshot.mock.calls[0] as unknown as [
      { preferenceLayers: unknown[] },
    ];
    expect(layout.displayPreferences.length).toBeGreaterThan(1);
    expect(preferenceLayers).toEqual(layout.displayPreferences);
  });

  afterEach(() => {
    vi.unstubAllEnvs();
    renderPlotSnapshot.mockClear();
    getUISettings.mockClear();
    getStatus.mockClear();
  });

  it("hands a document the tenant's chart settings and draws the snapshot with them", async () => {
    const data = await runLoad(vi.fn().mockResolvedValue({}));

    expect(data.initialUiSettings).toEqual({ features: { display: { focusHours: 6 } } });
    expect(data.initialPredictionsAvailable).toBe(true);
    expect(renderPlotSnapshot).toHaveBeenCalledWith(
      expect.objectContaining({ lookbackHours: 6, predictions: true })
    );
  });

  it("leaves a client navigation's chart settings to the client's queries", async () => {
    const data = await runLoad(vi.fn().mockResolvedValue({}), true, true);

    expect(getUISettings).not.toHaveBeenCalled();
    expect(getStatus).not.toHaveBeenCalled();
    expect(data.initialUiSettings).toBeNull();
  });

  it("renders the plot snapshot into a document request", async () => {
    const data = await runLoad(vi.fn().mockResolvedValue({}));

    expect(data.plotSnapshot).toBe("<svg></svg>");
    expect(renderPlotSnapshot).toHaveBeenCalledOnce();
  });

  it.each([
    ["a share host", { isShareHost: true }],
    ["a guest session", { isGuestSession: true }],
  ])("renders no plot snapshot for %s", async (_name, viewer) => {
    const data = await runLoad(vi.fn().mockResolvedValue({}), true, false, viewer);

    expect(data.plotSnapshot).toBeNull();
    expect(renderPlotSnapshot).not.toHaveBeenCalled();
  });

  it("renders no plot snapshot for a client navigation's data request", async () => {
    const data = await runLoad(vi.fn().mockResolvedValue({}), true, true);

    expect(data.plotSnapshot).toBeNull();
    expect(renderPlotSnapshot).not.toHaveBeenCalled();
  });

  it("asks only for the reachable tenants when a tenantless host picks a landing", async () => {
    vi.stubEnv("BASE_DOMAIN", "nocturne.run");
    const getMyTenants = vi.fn().mockResolvedValue([{ slug: "sleepy", displayName: "Sleepy" }]);

    const landing = load({
      locals: { apiClient: { myTenants: { getMyTenants } } },
      request: new Request("https://nocturne.run/", { headers: { host: "nocturne.run" } }),
      parent: async () => ({ tenantless: true, baseDomain: "nocturne.run", dashboardSlugs: [] }),
    } as unknown as LoadEvent);

    await expect(landing).rejects.toMatchObject({ status: 303, location: "https://sleepy.nocturne.run/" });
    expect(getMyTenants).toHaveBeenCalledWith(true);
  });

  it("asks for both chart windows without the heart-rate and step series", async () => {
    const getDashboardChartData = vi.fn().mockResolvedValue({});

    const data = await runLoad(getDashboardChartData);
    await data.streamed.historicalChartData;

    expect(getDashboardChartData).toHaveBeenCalledTimes(2);
    for (const call of getDashboardChartData.mock.calls) expect(call[3]).toBe(false);
  });

  it("does not rerun the layout loads through parent() on a tenant host", async () => {
    vi.stubEnv("BASE_DOMAIN", "nocturne.run");
    const parent = vi.fn();

    await load({
      locals: {
        apiClient: { chartData: { getDashboardChartData: vi.fn().mockResolvedValue({}) }, ...settingsClients },
        isAuthenticated: true,
      },
      request: new Request("https://sleepy.nocturne.run/", { headers: { host: "sleepy.nocturne.run" } }),
      parent,
      cookies,
    } as unknown as LoadEvent);

    expect(parent).not.toHaveBeenCalled();
  });

  it("asks for no chart data for a viewer the layout redirects to sign-in", async () => {
    const getDashboardChartData = vi.fn().mockResolvedValue({});

    await runLoad(getDashboardChartData, false);

    expect(getDashboardChartData).not.toHaveBeenCalled();
  });

  it("starts the historical window before the initial window resolves", async () => {
    const releases: ((value: unknown) => void)[] = [];
    const getDashboardChartData = vi.fn(() => new Promise((resolve) => releases.push(resolve)));

    const pending = runLoad(getDashboardChartData);
    await vi.waitFor(() => expect(getDashboardChartData).toHaveBeenCalledTimes(2), { timeout: 500 });
    for (const release of releases) release({});
    await pending;
  });
});
