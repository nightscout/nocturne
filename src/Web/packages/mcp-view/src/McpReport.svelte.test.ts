import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import type { App } from "@modelcontextprotocol/ext-apps";
import "./app.css";
import McpReport from "./McpReport.svelte";

const from = Date.UTC(2026, 9, 1);
const to = Date.UTC(2026, 9, 29);

/** What render_report returned: one statistics panel, its spec normalised. */
const spec = {
  version: 1,
  title: null,
  range: null,
  panels: [
    {
      id: "p1",
      title: null,
      dataset: { name: "glucose_statistics" },
      primitive: "stat_tiles",
      bindings: { x: null, y: null, band: null, columns: ["mean"], series: null },
      range: { hours: 672, from: null, to: null },
    },
  ],
};
const report = (mean: number) => ({
  spec,
  units: "mg/dl",
  summary: "stats",
  panels: [
    {
      id: "p1",
      dataset: "glucose_statistics",
      primitive: "stat_tiles",
      bindings: { columns: ["mean"] },
      status: "ok",
      window: { from, to },
      fields: [{ name: "mean", label: "Mean glucose", kind: "number", unit: "mg/dL", description: "" }],
      rows: [{ mean }],
      summary: `Mean ${mean} mg/dL.`,
      link: "https://dev.nocturne.localhost/reports/glucose-distribution?from=2026-10-01&to=2026-10-28",
    },
  ],
});

type ToolResult = { structuredContent?: unknown; isError?: boolean; content: unknown[] };

/** A host stub: the App the view talks to, with the capabilities a host grants and its calls recorded. */
function stubHost(capabilities: Record<string, unknown>) {
  const host = {
    ontoolresult: undefined as ((params: { structuredContent?: unknown }) => void) | undefined,
    onhostcontextchanged: undefined as unknown,
    connect: vi.fn(async () => {}),
    getHostContext: () => ({ availableDisplayModes: ["inline"] }),
    getHostCapabilities: () => capabilities,
    callServerTool: vi.fn(async (): Promise<ToolResult> => ({ structuredContent: report(160), content: [] })),
    updateModelContext: vi.fn(async () => ({})),
    openLink: vi.fn(async () => ({})),
    requestDisplayMode: vi.fn(async () => ({ mode: "inline" })),
  };
  return { host, app: host as unknown as App };
}

describe("McpReport", () => {
  it("calls refine_report with the normalised spec the tool returned, and draws the answer", async () => {
    const { host, app } = stubHost({ serverTools: {}, openLinks: {} });
    render(McpReport, { app });
    await vi.waitFor(() => expect(host.ontoolresult).toBeTypeOf("function"));
    host.ontoolresult!({ structuredContent: report(150) });

    await page.getByRole("button", { name: "Load earlier data" }).click();

    expect(host.callServerTool).toHaveBeenCalledWith({
      name: "refine_report",
      arguments: {
        spec,
        patch: { panels: [{ id: "p1", range: { from: new Date(from - (to - from)).toISOString(), to: new Date(to).toISOString() } }] },
      },
    });
    await expect.element(page.getByText("Mean 160 mg/dL.")).toBeVisible();

    await page.getByRole("button", { name: "Open in Nocturne" }).click();
    expect(host.openLink).toHaveBeenCalledWith({ url: report(150).panels[0].link });
  });

  it("offers no control the host did not grant, and calls nothing", async () => {
    const { host, app } = stubHost({});
    render(McpReport, { app });
    await vi.waitFor(() => expect(host.ontoolresult).toBeTypeOf("function"));
    host.ontoolresult!({ structuredContent: report(150) });

    await expect.element(page.getByText("Mean 150 mg/dL.")).toBeVisible();
    expect(page.getByRole("button", { name: "Load earlier data" }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Open in Nocturne" }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Show full screen" }).elements()).toHaveLength(0);
    expect(host.callServerTool).not.toHaveBeenCalled();
  });

  it("shows the API's list of what to change when a refinement is refused", async () => {
    const { host, app } = stubHost({ serverTools: {} });
    host.callServerTool.mockResolvedValueOnce({
      isError: true,
      content: [{ type: "text", text: "The refined report spec is not valid.\n- panels[0].range: covers at most 90 days" }],
    });
    render(McpReport, { app });
    await vi.waitFor(() => expect(host.ontoolresult).toBeTypeOf("function"));
    host.ontoolresult!({ structuredContent: report(150) });

    await page.getByRole("button", { name: "Load earlier data" }).click();

    await expect.element(page.getByRole("alert")).toHaveTextContent("covers at most 90 days");
    await expect.element(page.getByText("Mean 150 mg/dL.")).toBeVisible();
  });
});
