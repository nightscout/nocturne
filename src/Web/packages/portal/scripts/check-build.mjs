// Fails the deploy when the prerendered site has no copy. A build whose
// translations compile to empty strings still succeeds and prerenders blank
// headings, so the workflow reported success on a blank site for hours before
// this guard existed. Run after `pnpm run build`, before uploading the
// artifact. Pass a build directory as the first argument (defaults to `build`).

import { readFileSync, existsSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const buildDir = resolve(process.argv[2] ?? resolve(here, "../build"));

const PAGES = ["index.html", "get-involved.html", "features.html", "faq.html", "docs.html"];
const MIN_VISIBLE_CHARS = 400;

/** SvelteKit may emit `features.html` or `features/index.html`; take whichever exists. */
function pagePath(page) {
  const flat = resolve(buildDir, page);
  if (existsSync(flat)) return flat;
  const nested = resolve(buildDir, page.replace(/\.html$/, ""), "index.html");
  if (existsSync(nested)) return nested;
  return null;
}

function stripTags(html) {
  return html.replace(/<[^>]*>/g, "");
}

function visibleText(html) {
  const withoutScripts = html
    .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, "")
    .replace(/<style\b[^>]*>[\s\S]*?<\/style>/gi, "")
    .replace(/<!--[\s\S]*?-->/g, "");
  return stripTags(withoutScripts).replace(/\s+/g, " ").trim();
}

/** Returns the visible-text length on success, or a `{ reason }` on failure. */
function checkPage(html) {
  const text = visibleText(html);
  if (text.length < MIN_VISIBLE_CHARS) {
    return { reason: `only ${text.length} chars of visible text (need ${MIN_VISIBLE_CHARS})` };
  }

  const h1 = html.match(/<h1\b[^>]*>([\s\S]*?)<\/h1>/i);
  if (!h1) return { reason: "no <h1> element" };
  if (!stripTags(h1[1]).replace(/\s+/g, " ").trim()) return { reason: "empty <h1>" };

  return { chars: text.length };
}

let failed = false;

for (const page of PAGES) {
  const path = pagePath(page);
  if (!path) {
    console.log(`FAIL ${page}  not found in ${buildDir}`);
    failed = true;
    continue;
  }

  const result = checkPage(readFileSync(path, "utf8"));
  if (result.reason) {
    console.log(`FAIL ${page}  ${result.reason}`);
    failed = true;
  } else {
    console.log(`ok   ${page}  ${result.chars} chars`);
  }
}

process.exit(failed ? 1 : 0);
