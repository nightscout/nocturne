/**
 * Runs a HyperFrames command over compositions: `node scripts/hyperframes.js <check|render> [name...]`.
 * With no names it covers every composition; `render` writes renders/<name>.mp4.
 */
import { spawnSync } from "node:child_process";
import { readdirSync } from "node:fs";

const [command, ...names] = process.argv.slice(2);
if (!["check", "render"].includes(command)) {
  console.error("usage: node scripts/hyperframes.js <check|render> [name...]");
  process.exit(1);
}

const all = readdirSync(new URL("../compositions/", import.meta.url), { withFileTypes: true })
  .filter((entry) => entry.isDirectory())
  .map((entry) => entry.name);
const chosen = names.length ? names : all;

let failed = false;
for (const name of chosen) {
  const args = [command, `compositions/${name}`];
  if (command === "render") args.push("--quality", "high", "--output", `renders/${name}.mp4`);
  const { status } = spawnSync("pnpm", ["exec", "hyperframes", ...args], { stdio: "inherit", shell: true });
  failed ||= status !== 0;
}
process.exit(failed ? 1 : 0);
