import { createHash } from "node:crypto";
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import { loadLocales, runWithLocale } from "wuchale/load-utils/server";
import * as main from "../../../../../../../locales/main.loader.server.svelte.js";
import * as js from "../../../../../../../locales/js.loader.server.js";
import { locales } from "../../../../../../../locales/data.js";
import { timeSeriesPanel } from "#lib/server/report-image/report-image.fixtures.js";

vi.mock("$app/env/private", () => ({
  get INSTANCE_KEY() {
    return process.env.INSTANCE_KEY;
  },
}));

/** Set by a test to stand for a render pool with every worker drawing and its queue full. */
const pool = vi.hoisted(() => ({ busy: false }));
vi.mock("#lib/server/report-image/render.js", async (original) => {
  const actual = await original<typeof import("#lib/server/report-image/render.js")>();
  const { RenderPoolBusyError } = await import("#lib/server/report-image/pool.js");
  return {
    ...actual,
    renderReportImage: (request: Parameters<typeof actual.renderReportImage>[0]) =>
      pool.busy ? Promise.reject(new RenderPoolBusyError()) : actual.renderReportImage(request),
  };
});

const { POST } = await import("./+server.js");

const KEY = "report-image-test-key";
const DIGEST = createHash("sha256").update(KEY).digest("hex");
const service = { "X-Instance-Key": DIGEST, "X-Instance-Service": "nocturne-api" };

async function post(body: unknown, headers: Record<string, string> = service): Promise<Response> {
  const request = new Request("http://web.internal/api/internal/report-image", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: body instanceof ReadableStream ? body : typeof body === "string" ? body : JSON.stringify(body),
    ...(body instanceof ReadableStream ? { duplex: "half" } : {}),
  });
  const event = { request } as Parameters<typeof POST>[0];
  return await runWithLocale("en", async () => await POST(event));
}

const previous = process.env.INSTANCE_KEY;
beforeAll(async () => {
  process.env.INSTANCE_KEY = KEY;
  await loadLocales(main.key, main.loadCount, main.loadCatalog, locales);
  await loadLocales(js.key, js.loadCount, js.loadCatalog, locales);
});
afterAll(() => {
  if (previous === undefined) delete process.env.INSTANCE_KEY;
  else process.env.INSTANCE_KEY = previous;
});

const oneChart = { panel: timeSeriesPanel, units: "mg/dl", timeZone: "Etc/UTC" };

describe("POST /api/internal/report-image", () => {
  it.each([
    ["no service headers", {}],
    ["the key without the service marker", { "X-Instance-Key": DIGEST }],
    ["the service marker without the key", { "X-Instance-Service": "nocturne-api" }],
    ["a wrong key", { "X-Instance-Key": createHash("sha256").update("guess").digest("hex"), "X-Instance-Service": "nocturne-api" }],
    ["the raw key in place of its digest", { "X-Instance-Key": KEY, "X-Instance-Service": "nocturne-api" }],
  ])("refuses a request with %s, before it reads the body", async (_, headers) => {
    const response = await post("not json at all", headers);
    expect(response.status).toBe(401);
    expect(await response.text()).toBe("");
  });

  it("refuses every request when the instance has no key", async () => {
    delete process.env.INSTANCE_KEY;
    try {
      expect((await post(oneChart)).status).toBe(401);
    } finally {
      process.env.INSTANCE_KEY = KEY;
    }
  });

  it("draws the panel as a PNG for the service", async () => {
    const response = await post({ ...oneChart, width: 640, height: 320, theme: "dark", devicePixelRatio: 1 });

    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toBe("image/png");
    expect(response.headers.get("cache-control")).toBe("no-store");
    const png = Buffer.from(await response.arrayBuffer());
    expect(png.subarray(1, 4).toString("ascii")).toBe("PNG");
    expect([png.readUInt32BE(16), png.readUInt32BE(20)]).toEqual([640, 320]);
  });

  it("answers 400 with the reason for a request it cannot draw", async () => {
    const response = await post({ ...oneChart, width: 99999 });
    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ error: "width: Send a whole number from 320 to 2400." });
    expect((await post("{not json")).status).toBe(400);
  });

  it("refuses a body over the limit, reading no more than the limit of it", async () => {
    const chunk = new Uint8Array(64 * 1024).fill(0x20);
    let sent = 0;
    const endless = new ReadableStream<Uint8Array>({
      pull(controller) {
        if (sent > 8 * 1024 * 1024) controller.error(new Error("read past the limit"));
        sent += chunk.byteLength;
        controller.enqueue(chunk);
      },
    });

    const response = await post(endless);

    expect(response.status).toBe(413);
    expect(sent).toBeLessThanOrEqual(4 * 1024 * 1024 + 2 * chunk.byteLength);
  });

  it("refuses a panel with more points than one image draws, as too large", async () => {
    const rows = Array.from({ length: 12_001 }, (_, i) => ({ ...timeSeriesPanel.rows[0], time: timeSeriesPanel.rows[0].time + i }));
    const response = await post({ ...oneChart, panel: { ...timeSeriesPanel, rows } });
    expect(response.status).toBe(413);
    expect((await response.json()).error).toContain("more than the 12000 one image draws");
  });

  it("answers 503 when every render worker is drawing and the queue is full", async () => {
    pool.busy = true;
    try {
      const response = await post(oneChart);
      expect(response.status).toBe(503);
      expect(await response.json()).toEqual({ error: "The image renderer is busy. Try again." });
    } finally {
      pool.busy = false;
    }
  });
});
