// Copies the published docs videos and their posters into the portal's static/ tree, since
// adapter-static only emits assets it owns.

import { cpSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { basename, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const src = dirname(fileURLToPath(import.meta.resolve("@nocturne/video/manifest.json")));
const dest = resolve(here, "../static/videos");

rmSync(dest, { recursive: true, force: true });
mkdirSync(dest, { recursive: true });

cpSync(src, dest, { recursive: true, filter: (path) => !basename(path).startsWith(".") && !path.endsWith(".json") });

console.log(`[videos] synced ${readdirSync(dest).length} file(s) to static/videos`);
