// Copies the documentation screenshots out of @nocturne/screenshots and into the
// portal's static/ tree, since adapter-static only emits assets it owns. Each
// manifest variant records its file as a package-root-relative path
// (images/<id>.<theme>.webp), so the tree is mirrored verbatim under
// static/screenshots/ and Screenshot.svelte can join the recorded path straight
// onto the URL. Output is gitignored; run via the dev/build scripts.

import { cpSync, existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { basename, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const packageRoot = dirname(fileURLToPath(import.meta.resolve("@nocturne/screenshots/manifest.json")));

const src = resolve(packageRoot, "images");
const destRoot = resolve(here, "../static/screenshots");
const dest = resolve(destRoot, "images");

rmSync(destRoot, { recursive: true, force: true });
mkdirSync(dest, { recursive: true });

// A checkout that has never run a capture has an empty manifest and an images/
// directory holding nothing but its .gitkeep, which is a valid state — no doc
// page can reference an image yet.
if (existsSync(src)) {
  cpSync(src, dest, { recursive: true, filter: (path) => !basename(path).startsWith(".") });
}

console.log(`[screenshots] synced ${readdirSync(dest).length} image(s) to static/screenshots/images`);
