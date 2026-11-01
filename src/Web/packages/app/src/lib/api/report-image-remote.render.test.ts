import { isHttpError } from "@sveltejs/kit";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AUTH_COOKIE_NAMES } from "#lib/config/auth-cookies.js";
import { stackedBarPanel, glucoseChartPanel } from "#lib/server/report-image/report-image.fixtures.js";

const state = vi.hoisted(() => ({
  event: null as unknown,
  runs: [] as unknown[],
  drawn: [] as { panel: { id: string; rows: unknown[] } }[],
  result: null as unknown,
}));

// The framework's command validates the argument with the schema, then calls the function.
vi.mock("$app/server", () => ({
  getRequestEvent: () => state.event,
  command: (schema: { parse: (v: unknown) => unknown }, fn: (arg: unknown) => unknown) => async (arg: unknown) => fn(schema.parse(arg)),
}));

vi.mock("#lib/server/report-image/render.js", async (original) => ({
  ...(await original<typeof import("#lib/server/report-image/render.js")>()),
  renderReportImage: async (request: { panel: { id: string; rows: unknown[] } }) => {
    state.drawn.push(request);
    return Buffer.from("png");
  },
}));

const { drawReportPanelImage } = await import("./report-image.remote.js");

const report = (panels: unknown[]) => ({
  spec: { version: 1, title: null }, units: "mg/dl", summary: "", warnings: [], timeZone: "UTC", panels,
});

let counter = 0;
function member(overrides: Record<string, unknown> = {}, cookies: Record<string, string> = { [AUTH_COOKIE_NAMES.accessToken]: "token" }) {
  counter++;
  return {
    url: new URL("https://dev.nocturne.localhost/reports/custom/a1"),
    cookies: { get: (name: string) => cookies[name] },
    locals: {
      isAuthenticated: true,
      isShareHost: false,
      user: { subjectId: `member-${counter}` },
      apiClient: {
        customReports: {
          runCustomReport: async (request: unknown) => {
            state.runs.push(request);
            return { result: state.result, spec: {}, errors: [] };
          },
        },
      },
      ...overrides,
    },
  };
}

async function refusal(call: () => Promise<unknown>): Promise<{ status: number; message: string }> {
  try {
    await call();
  } catch (err) {
    if (isHttpError(err)) return { status: err.status, message: err.body.message };
    throw err;
  }
  throw new Error("The command did not refuse.");
}

const spec = { version: 1, panels: [{ id: "glucose", dataset: { name: "glucose_series" }, kind: "table" }] };

describe("drawReportPanelImage", () => {
  beforeEach(() => {
    state.runs.length = 0;
    state.drawn.length = 0;
    state.result = report([glucoseChartPanel]);
  });

  it("runs the spec again through the API under the member's access and draws the panel of that result", async () => {
    state.event = member();

    const png = await drawReportPanelImage({ spec, panelId: "glucose" });

    expect(png).toBe(Buffer.from("png").toString("base64"));
    expect(state.runs).toEqual([{ spec }]);
    expect(state.drawn).toHaveLength(1);
    expect(state.drawn[0].panel.tracks).toEqual(glucoseChartPanel.tracks);
  });

  it("refuses a report result sent by the page, and draws nothing", async () => {
    state.event = member();
    const forged = report([{ ...glucoseChartPanel, tracks: [] }]);

    await expect(drawReportPanelImage({ result: forged, panelId: "glucose" } as never)).rejects.toThrow();
    await expect(drawReportPanelImage({ spec, result: forged, panelId: "glucose" } as never)).rejects.toThrow();
    expect(state.runs).toEqual([]);
    expect(state.drawn).toEqual([]);
  });

  it("draws what the API's run returns, not what the spec the page sent holds", async () => {
    state.event = member();
    state.result = report([{ ...stackedBarPanel, id: "glucose" }]);

    await drawReportPanelImage({ spec: report([{ ...glucoseChartPanel, tracks: [] }]), panelId: "glucose" });

    expect(state.drawn[0].panel.tracks).toEqual(stackedBarPanel.tracks);
  });

  it.each([
    ["a guest link's session (no member token)", () => member({}, { [AUTH_COOKIE_NAMES.guestSession]: "grant" })],
    ["a share host", () => member({ isShareHost: true })],
    ["no sign-in", () => member({ isAuthenticated: false, user: null })],
  ])("refuses %s before anything is run", async (_, event) => {
    state.event = event();

    expect(await refusal(() => drawReportPanelImage({ spec, panelId: "glucose" }))).toEqual({ status: 403, message: "not_member" });
    expect(state.runs).toEqual([]);
  });

  it("refuses a seventh image in a minute from one member", async () => {
    state.event = member();
    for (let i = 0; i < 6; i++) await drawReportPanelImage({ spec, panelId: "glucose" });

    expect(await refusal(() => drawReportPanelImage({ spec, panelId: "glucose" }))).toEqual({ status: 429, message: "rate_limited" });
    expect(state.runs).toHaveLength(6);
  });

  it("refuses a panel the run does not hold", async () => {
    state.event = member();
    expect(await refusal(() => drawReportPanelImage({ spec, panelId: "other" }))).toEqual({ status: 400, message: "not_drawable" });
  });
});
