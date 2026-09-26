// svelte-check needs what the app imports but does not build: vite.config.ts
// (loaded for style preprocessing) imports @nocturne/bridge, server code imports
// @nocturne/bot's types, and most components import the generated API client.
/* eslint-disable security/detect-non-literal-fs-filename -- every path is fixed relative to this file */
import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, statSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const app = dirname(dirname(fileURLToPath(import.meta.url)));

const generatedClient = join(
  app,
  "src/lib/api/generated/nocturne-api-client.ts"
);
if (!existsSync(generatedClient)) {
  console.error(
    "The generated API client is missing. Build the API once from the repo root:\n" +
      "  dotnet build src/API/Nocturne.API/Nocturne.API.csproj"
  );
  process.exit(1);
}

function newestMtime(path) {
  const stat = statSync(path);
  if (!stat.isDirectory()) return stat.mtimeMs;
  let newest = 0;
  for (const entry of readdirSync(path)) {
    newest = Math.max(newest, newestMtime(join(path, entry)));
  }
  return newest;
}

function isStale(pkg) {
  const dir = join(app, "..", pkg);
  const output = join(dir, "dist/index.d.ts");
  if (!existsSync(output)) return true;
  const inputs = ["src", "tsconfig.json", "package.json"].map((p) =>
    join(dir, p)
  );
  return Math.max(...inputs.map(newestMtime)) > statSync(output).mtimeMs;
}

const stale = ["bridge", "bot"].filter(isStale);
if (stale.length > 0) {
  const filters = stale.map((pkg) => `--filter @nocturne/${pkg}`).join(" ");
  // A shell resolves pnpm's .cmd shim on Windows.
  const { status } = spawnSync(`pnpm ${filters} run build`, {
    cwd: app,
    stdio: "inherit",
    shell: true,
  });
  if (status !== 0) process.exit(status ?? 1);
}
