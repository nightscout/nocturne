// Entry point for the e2e stack.
//
//   node scripts/stack.ts build             build the images whose inputs changed
//   node scripts/stack.ts up                build, start, seed a tenant and print how to reach it
//   node scripts/stack.ts down              stop and discard everything (the DB is on tmpfs)
//   node scripts/stack.ts run api|web|all   build, start fresh, run the specs, tear down
//
// E2E_KEEP_STACK=1 leaves the stack running after `run`. E2E_SKIP_BUILD=1 uses whatever
// nocturne-api:e2e / nocturne-web:e2e already exist (CI builds them in an earlier step).

import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { capture, compose, config, E2E_DIR, ensureImages, run } from "./lib.ts";

const PROJECT = process.env.E2E_PROJECT ?? "nocturne-e2e";
const [command = "run", target = "all"] = process.argv.slice(2);

async function up() {
  if (process.env.E2E_SKIP_BUILD !== "1") await ensureImages();
  // --force-recreate / --renew-anon-volumes: a run never inherits a previous run's containers.
  const code = await compose(PROJECT, [
    "up", "-d", "--wait", "--wait-timeout", "300",
    "--force-recreate", "--renew-anon-volumes", "--remove-orphans",
  ]);
  if (code !== 0) {
    await dumpLogs();
    throw new Error("the e2e stack did not become healthy; logs in e2e/docker-compose-logs.txt");
  }
}

async function down() {
  await compose(PROJECT, ["down", "--volumes", "--remove-orphans", "--timeout", "5"]);
}

async function dumpLogs() {
  const logs = capture("docker", ["compose", "-p", PROJECT, "-f", join(E2E_DIR, "docker-compose.yml"), "logs", "--no-color", "--timestamps"]);
  writeFileSync(join(E2E_DIR, "docker-compose-logs.txt"), logs);
}

async function seedForHumans() {
  const slug = `dev-${Math.random().toString(36).slice(2, 7)}`;
  const res = await fetch(`http://127.0.0.1:${config.apiPort}/api/v4/dev-only/admin/seed-tenant`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ slug, displayName: `Dev ${slug}`, ownerUsername: "dev", sampleData: true, sampleDataDays: 14 }),
  });
  if (!res.ok) throw new Error(`seed-tenant: HTTP ${res.status} ${await res.text()}`);
  const body = (await res.json()) as { accessToken: string };
  const web = `http://${slug}.nocturne.localhost:${config.webPort}`;
  console.log(`
e2e stack is up (compose project ${PROJECT})

  web          ${web}/
  login link   ${web}/api/v4/dev-only/auth/login?redirect=%2F
  api          http://127.0.0.1:${config.apiPort}  (send Host: ${slug}.nocturne.localhost:${config.webPort})
  token        ${body.accessToken}
  postgres     postgresql://postgres:e2e-postgres-password@127.0.0.1:${config.postgresPort}/nocturne
  fake vendors http://127.0.0.1:${config.mocksPort}/nightscout

Stop with: pnpm e2e:down
`);
}

async function runSpecs(which: string): Promise<number> {
  let failed = 0;
  if (which === "api" || which === "all") {
    failed += (await run("pnpm", ["exec", "vitest", "run"], { cwd: E2E_DIR })) === 0 ? 0 : 1;
  }
  if (which === "web" || which === "all") {
    failed += (await run("pnpm", ["exec", "playwright", "test"], { cwd: E2E_DIR })) === 0 ? 0 : 1;
  }
  return failed;
}

async function main() {
  switch (command) {
    case "build":
      await ensureImages({
        force: process.argv.includes("--force"),
        only: process.argv.includes("--api") ? "api" : process.argv.includes("--web") ? "web" : undefined,
      });
      return;
    case "up":
      await up();
      await seedForHumans();
      return;
    case "down":
      await down();
      return;
    case "run": {
      if (!["api", "web", "all"].includes(target)) throw new Error(`unknown target '${target}'`);
      const started = Date.now();
      await down();
      await up();
      const upAt = Date.now();
      console.log(`[e2e] stack healthy in ${Math.round((upAt - started) / 1000)}s`);
      let failures = 1;
      try {
        failures = await runSpecs(target);
      } finally {
        if (failures > 0) await dumpLogs();
        if (process.env.E2E_KEEP_STACK !== "1") await down();
      }
      console.log(`[e2e] specs ${Math.round((Date.now() - upAt) / 1000)}s, total ${Math.round((Date.now() - started) / 1000)}s`);
      process.exitCode = failures > 0 ? 1 : 0;
      return;
    }
    default:
      throw new Error(`unknown command '${command}'`);
  }
}

main().catch((err) => {
  console.error(`[e2e] ${err instanceof Error ? err.message : err}`);
  process.exit(1);
});
