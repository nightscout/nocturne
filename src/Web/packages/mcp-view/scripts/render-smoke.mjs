// Renders the built dist/report.html the way an MCP Apps host does: in a sandboxed iframe under
// the spec's default CSP, behind a minimal host that answers ui/initialize and sends one
// report result (a dashboard chart panel and a time series panel). The vitest suite runs the unbundled sources, so only this sees the bundle.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const CSP =
  "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
  "img-src 'self' data:; media-src 'self' data:; connect-src 'none'";
const READINGS = 36;
const from = Date.UTC(2026, 9, 29, 6);
const window_ = { from, to: from + 3 * 3600_000 };
const glucoseData = Array.from({ length: READINGS }, (_, i) => ({ time: from + i * 300_000, sgv: 90 + i * 5 }));
const field = (name, label, kind, unit, extra = {}) => ({ name, label, kind, unit, description: label, ...extra });
const result = {
  content: [{ type: "text", text: "summary" }],
  structuredContent: {
    spec: { version: 1, title: "Smoke" },
    units: "mg/dl",
    summary: "summary",
    warnings: [],
    panels: [
      {
        id: "chart",
        dataset: "glucose_series",
        primitive: "glucose_chart",
        bindings: {},
        status: "ok",
        window: window_,
        fields: [],
        rows: [],
        omittedScopes: [],
        summary: "chart summary",
        chart: {
          glucoseData,
          thresholds: { low: 70, high: 180, veryLow: 54, veryHigh: 250, glucoseYMax: 300 },
        },
      },
      {
        id: "line",
        dataset: "glucose_series",
        primitive: "time_series",
        bindings: { x: "time", y: ["glucose"], band: ["targetLow", "targetHigh"] },
        status: "ok",
        window: window_,
        fields: [
          field("time", "Time", "time", null),
          field("glucose", "Glucose", "number", "mg/dL", { color: "glucose-in-range", glucoseScale: true }),
          field("targetLow", "Range low", "number", "mg/dL"),
          field("targetHigh", "Range high", "number", "mg/dL"),
        ],
        rows: glucoseData.map((r) => ({ time: r.time, glucose: r.sgv, targetLow: 70, targetHigh: 180 })),
        thresholds: { veryLow: 54, low: 70, high: 180, veryHigh: 250, mgdlPerUnit: 1 },
        summary: "line summary",
      },
    ],
  },
};

// eslint-disable-next-line security/detect-non-literal-fs-filename -- fixed relative to this file
const html = readFileSync(fileURLToPath(import.meta.resolve("../dist/report.html")), "utf8").replace(
  "<head>",
  `<head><meta http-equiv="Content-Security-Policy" content="${CSP}">`,
);

const browser = await chromium.launch();
const page = await browser.newPage();
const errors = [];
page.on("pageerror", (e) => errors.push(e.message));
page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
await page.setContent(`<iframe id="view" sandbox="allow-scripts" style="width:880px;height:540px"></iframe>`);
await page.evaluate(
  ({ html, result }) => {
    const frame = document.getElementById("view");
    window.addEventListener("message", (event) => {
      if (event.source !== frame.contentWindow) return;
      const message = event.data;
      const send = (reply) => frame.contentWindow.postMessage({ jsonrpc: "2.0", ...reply }, "*");
      if (message.method === "ui/initialize") {
        send({
          id: message.id,
          result: {
            protocolVersion: message.params.protocolVersion,
            hostInfo: { name: "smoke", version: "0" },
            hostCapabilities: {},
            hostContext: { theme: "dark", displayMode: "inline", locale: "en-GB" },
          },
        });
      } else if (message.method === "ui/notifications/initialized") {
        send({ method: "ui/notifications/tool-result", params: result });
      } else if (message.id !== undefined && message.method) {
        send({ id: message.id, result: {} });
      }
    });
    frame.srcdoc = html;
  },
  { html, result },
);

const plot = page.frameLocator("#view").getByTestId("glucose-plot");
let circles = 0;
let lines;
try {
  await plot.waitFor({ timeout: 15_000 });
  for (let i = 0; i < 30 && circles < READINGS; i++) {
    circles = await plot.locator("circle").count();
    await page.waitForTimeout(100);
  }
  const series = page.frameLocator("#view").getByTestId("report-time-series");
  await series.waitFor({ timeout: 5_000 });
  lines = await series.locator("path.fill-none").count();
} finally {
  await browser.close();
}

if (errors.length > 0 || circles < READINGS || !(lines >= 1)) {
  console.error(`render smoke failed: ${circles} of ${READINGS} points drawn, ${lines} time-series lines`);
  for (const error of errors) console.error(`  ${error}`);
  process.exit(1);
}
console.log(`render smoke passed: ${circles} points drawn, ${lines} time-series lines, no errors`);
