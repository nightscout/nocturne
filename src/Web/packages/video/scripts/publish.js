/**
 * Turns rendered films into what the docs embed: `node scripts/publish.js [id...]` writes
 * published/<id>.mp4 (re-encoded for the web, playable before it has fully downloaded),
 * published/<id>.webp (the title card, as the poster) and published/manifest.json. An id is a
 * composition name: a report (`agp`) or a feature film (`feature-food`). Needs ffmpeg on PATH and
 * a current render in renders/<id>.mp4; with no ids it publishes every film that has a script.
 *
 * The published files are committed, as the docs screenshots are, so the portal build needs
 * neither a render nor ffmpeg.
 */
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { features } from "../src/features.js";
import { reports } from "../src/reports.js";
import { INTRO, schedule } from "../src/tour/timing.js";

const path = (relative) => fileURLToPath(new URL(relative, import.meta.url));
const out = path("../published/");
const manifestPath = `${out}manifest.json`;

/** Every film by composition name, with the path of its script. */
const films = Object.fromEntries([
  ...Object.keys(reports).map((name) => [name, `../src/films/${name}.js`]),
  ...Object.keys(features).map((name) => [`feature-${name}`, `../src/features/films/${name}.js`]),
]);

const only = process.argv.slice(2);
const unknown = only.filter((id) => !films[id]);
if (unknown.length) throw new Error(`Not a film: ${unknown.join(", ")}. Films: ${Object.keys(films).join(", ")}`);
const names = Object.keys(films).filter((id) => (only.length ? only.includes(id) : existsSync(path(films[id]))));
mkdirSync(out, { recursive: true });
const manifest = existsSync(manifestPath) ? JSON.parse(readFileSync(manifestPath, "utf8")) : {};

function ffmpeg(args) {
  const { status, stderr } = spawnSync("ffmpeg", ["-v", "error", "-y", ...args], { encoding: "utf8" });
  if (status !== 0) throw new Error(`ffmpeg ${args.join(" ")}\n${stderr}`);
}

for (const name of names) {
  const render = path(`../renders/${name}.mp4`);
  if (!existsSync(render)) throw new Error(`No render of ${name}: run \`pnpm run render -- ${name}\` first.`);
  const { default: film } = await import(films[name]);

  // Report UI is mostly still between moves, so a high CRF stays sharp at a fraction of the size.
  ffmpeg(["-i", render, "-c:v", "libx264", "-preset", "slow", "-crf", "28", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an", `${out}${name}.mp4`]);
  // The title card is fully in by then and still on screen.
  ffmpeg(["-ss", String(INTRO - 0.8), "-i", render, "-frames:v", "1", "-vf", "scale=1280:-1", "-quality", "80", `${out}${name}.webp`]);

  manifest[name] = {
    title: film.intro.title,
    summary: film.intro.line,
    duration: schedule(film).total,
    width: 1920,
    height: 1080,
    video: `${name}.mp4`,
    poster: `${name}.webp`,
    transcript: film.scenes.map((scene) => ({ heading: scene.heading, notes: scene.beats.map((beat) => beat.note) })),
  };
  console.log(`published ${name}`);
}

const sorted = Object.fromEntries(Object.keys(manifest).sort().map((key) => [key, manifest[key]]));
writeFileSync(manifestPath, JSON.stringify(sorted, null, 2) + "\n");
