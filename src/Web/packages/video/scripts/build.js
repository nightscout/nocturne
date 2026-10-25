/**
 * Builds one bundle per composition, since an IIFE bundle has a single entry, and writes each
 * film's composition around it (`composition.js`). With no names on the command line it builds
 * every report that has a film, and the logo:
 *
 *   node scripts/build.js [--features] [name...]
 *
 * `--features` builds feature films (`compositions/feature-<name>/`) in place of reports.
 */
import { spawnSync } from "node:child_process";
import { cpSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { build } from "vite";
import { writeComposition } from "./composition.js";
import { kindOf } from "./kind.js";

const path = (relative) => fileURLToPath(new URL(relative, import.meta.url));

const kind = kindOf(process.argv.slice(2));
const only = kind.names;

// The report bundles carry the logo themselves; this is the logo composition's own bundle.
if (!kind.forFeatures && (!only.length || only.includes("logo"))) {
  await build({
    configFile: false,
    logLevel: "warn",
    build: {
      outDir: path("../compositions/logo/vendor"),
      emptyOutDir: true,
      lib: { entry: path("../src/logo/index.js"), name: "NocturneLogo", formats: ["iife"], fileName: () => "logo.js" },
    },
  });
  cpSync(path("../../app/static/fonts"), path("../compositions/logo/fonts"), { recursive: true });
  console.log("built logo");
}

const names = Object.keys(kind.list).filter((name) => (only.length ? only.includes(name) : kind.hasFilm(name)));

// Each bundle carries the whole app, and one process building many runs out of heap, so each
// gets its own.
if (names.length > 1) {
  let failed = false;
  for (const name of names) {
    const args = [path("./build.js"), ...(kind.flag ? [kind.flag] : []), name];
    const { status } = spawnSync(process.execPath, args, { stdio: "inherit" });
    failed ||= status !== 0;
  }
  process.exit(failed ? 1 : 0);
}

for (const name of names) {
  process.env[kind.env] = name;
  await build({ configFile: path(`../${kind.config}`), logLevel: "warn" });

  if (!kind.hasFilm(name)) {
    console.log(`built ${name}: no film yet`);
    continue;
  }
  const total = writeComposition(kind.composition(name), await kind.loadFilm(name), kind.filmOf(name));
  console.log(`built ${name}: ${total}s`);
}
