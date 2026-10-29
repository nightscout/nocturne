// Renders the built dist/report.html the way an MCP Apps host does: in a sandboxed iframe under
// the spec's default CSP, behind a minimal host that answers ui/initialize and sends one
// show_glucose result. The vitest suite runs the unbundled sources, so only this sees the bundle.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const CSP =
  "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
  "img-src 'self' data:; media-src 'self' data:; connect-src 'none'";
const READINGS = 36;
const from = Date.UTC(2026, 9, 29, 6);
const result = {
  content: [{ type: "text", text: "summary" }],
  structuredContent: {
    kind: "glucose",
    units: "mmol",
    window: { from, to: from + 3 * 3600_000 },
    omittedTracks: [],
    data: {
      glucoseData: Array.from({ length: READINGS }, (_, i) => ({ time: from + i * 300_000, sgv: 90 + i * 5 })),
      thresholds: { low: 70, high: 180, veryLow: 54, veryHigh: 250, glucoseYMax: 300 },
    },
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
try {
  await plot.waitFor({ timeout: 15_000 });
  for (let i = 0; i < 30 && circles < READINGS; i++) {
    circles = await plot.locator("circle").count();
    await page.waitForTimeout(100);
  }
} finally {
  await browser.close();
}

if (errors.length > 0 || circles < READINGS) {
  console.error(`render smoke failed: ${circles} of ${READINGS} points drawn`);
  for (const error of errors) console.error(`  ${error}`);
  process.exit(1);
}
console.log(`render smoke passed: ${circles} points drawn, no errors`);
