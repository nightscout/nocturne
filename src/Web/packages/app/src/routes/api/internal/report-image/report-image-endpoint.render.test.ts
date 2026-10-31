import { createHash } from "node:crypto";
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import { loadLocales, runWithLocale } from "wuchale/load-utils/server";
import * as main from "../../../../../../../locales/main.loader.server.svelte.js";
import * as js from "../../../../../../../locales/js.loader.server.js";
import { locales } from "../../../../../../../locales/data.js";
import { everyPrimitive, timeSeriesPanel } from "#lib/server/report-image/report-image.fixtures.js";

vi.mock("$app/env/private", () => ({
  get INSTANCE_KEY() {
    return process.env.INSTANCE_KEY;
  },
}));

const { POST } = await import("./+server.js");

const KEY = "report-image-test-key";
const DIGEST = createHash("sha256").update(KEY).digest("hex");
const service = { "X-Instance-Key": DIGEST, "X-Instance-Service": "nocturne-api" };

async function post(body: unknown, headers: Record<string, string> = service): Promise<Response> {
  const request = new Request("http://web.internal/api/internal/report-image", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: typeof body === "string" ? body : JSON.stringify(body),
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

const oneChart = { ...everyPrimitive, panels: [timeSeriesPanel] };

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
      expect((await post({ result: oneChart })).status).toBe(401);
    } finally {
      process.env.INSTANCE_KEY = KEY;
    }
  });

  it("draws the panel as a PNG for the service", async () => {
    const response = await post({ result: oneChart, panelId: "glucose", width: 640, height: 320, theme: "dark", devicePixelRatio: 1 });

    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toBe("image/png");
    expect(response.headers.get("cache-control")).toBe("no-store");
    const png = Buffer.from(await response.arrayBuffer());
    expect(png.subarray(1, 4).toString("ascii")).toBe("PNG");
    expect([png.readUInt32BE(16), png.readUInt32BE(20)]).toEqual([640, 320]);
  });

  it("answers 400 with the reason for a request it cannot draw", async () => {
    const response = await post({ result: oneChart, width: 99999 });
    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ error: "width: Send a whole number from 320 to 2400." });
    expect((await post("{not json")).status).toBe(400);
  });

  it("refuses a body over the limit without reading all of it", async () => {
    const response = await post({ result: oneChart, padding: "x".repeat(4 * 1024 * 1024 + 1) });
    expect(response.status).toBe(413);
  });
});
