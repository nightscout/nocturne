// Copies the documentation screenshots into the portal's static/ tree, since
// adapter-static only emits assets it owns.

import { cpSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { basename, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const packageRoot = dirname(fileURLToPath(import.meta.resolve("@nocturne/screenshots/manifest.json")));

const src = resolve(packageRoot, "images");
const destRoot = resolve(here, "../static/screenshots");
const dest = resolve(destRoot, "images");

rmSync(destRoot, { recursive: true, force: true });
mkdirSync(dest, { recursive: true });

cpSync(src, dest, { recursive: true, filter: (path) => !basename(path).startsWith(".") });

console.log(`[screenshots] synced ${readdirSync(dest).length} image(s) to static/screenshots/images`);
