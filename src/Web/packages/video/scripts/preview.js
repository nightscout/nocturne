/**
 * Mounts a built composition as its film shows it, with no film, and reports what a film can point
 * at: `node scripts/preview.js [--features] <name> <screenshot.png>`. Prints each card's title and
 * charts, the outline of each card, the test ids and labels on the page, and any remote function the
 * fixture lacks; saves a full-page screenshot.
 */
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "@playwright/test";
import { kindOf } from "./kind.js";

const kind = kindOf(process.argv.slice(2));
const [name, out] = kind.names;
if (!kind.list[name] || !out) {
  console.error(`usage: node scripts/preview.js [--features] <${Object.keys(kind.list).join("|")}> <screenshot.png>`);
  process.exit(1);
}

const dir = fileURLToPath(new URL(`../compositions/${kind.composition(name)}/`, import.meta.url));
const PAGE = `<!doctype html><html lang="en" class="dark"><head><meta charset="UTF-8" />
<link rel="stylesheet" href="vendor/report.css" /><script src="vendor/report.js"></script>
<style>html,body{width:auto;height:auto;overflow:visible} .tour-report{width:1440px}</style></head>
<body><div class="tour-report" id="report"></div><script>NocturneTour.preview(document.getElementById("report"));</script></body></html>`;
const TYPES = { ".js": "text/javascript", ".css": "text/css", ".ttf": "font/ttf" };

const server = createServer(async (req, res) => {
  const url = new URL(req.url, "http://localhost");
  if (url.pathname === "/") return res.writeHead(200, { "content-type": "text/html" }).end(PAGE);
  try {
    // Every path is under the composition directory; decodeURIComponent keeps the font's comma.
    const body = await readFile(dir + decodeURIComponent(url.pathname).replace(/^\/+/, "").replaceAll("..", ""));
    res.writeHead(200, { "content-type": TYPES[extname(url.pathname)] ?? "application/octet-stream" }).end(body);
  } catch {
    res.writeHead(404).end();
  }
});
await new Promise((resolve) => server.listen(0, resolve));

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
page.on("console", (message) => {
  if (message.text().startsWith("[replay]") || message.type() === "error") console.log(`console: ${message.text()}`);
});
page.on("pageerror", (error) => console.log(`page error: ${error.message}`));
await page.goto(`http://localhost:${server.address().port}/`, { waitUntil: "load" });
await page.waitForTimeout(1500);

const { cards, ids, labels } = await page.evaluate(() => ({
  cards: [...document.querySelectorAll('[data-slot="card"]')].map((card) => ({
    title: card.querySelector('[data-slot="card-title"]')?.textContent.trim() ?? "(untitled)",
    charts: card.querySelectorAll(".lc-root-container").length,
    outline: card.textContent.replace(/\s+/g, " ").trim().slice(0, 90),
  })),
  ids: [...document.querySelectorAll("[data-testid]")].map((node) => node.dataset.testid),
  labels: [...document.querySelectorAll("[aria-label], h1, h2, h3")].map(
    (node) => `${node.tagName.toLowerCase()} ${node.getAttribute("aria-label") ?? node.textContent.trim().slice(0, 60)}`
  ),
}));
cards.forEach(({ title, charts, outline }, i) => {
  console.log(`card ${i}: ${title}${charts ? ` (${charts} chart${charts > 1 ? "s" : ""})` : ""}`);
  console.log(`  ${outline}`);
});
console.log(`test ids: ${[...new Set(ids)].join(", ")}`);
console.log(`labels: ${[...new Set(labels)].join(" | ")}`);
await page.screenshot({ path: out, fullPage: true });
console.log(`screenshot: ${out}`);

await browser.close();
server.close();
