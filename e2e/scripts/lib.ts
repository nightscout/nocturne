// Shared plumbing for the e2e scripts: locating docker, running commands, and building the two
// production images only when their inputs changed.

import { spawn, spawnSync, type SpawnOptions } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const E2E_DIR = resolve(dirname(fileURLToPath(import.meta.url)), "..");
export const REPO_ROOT = resolve(E2E_DIR, "..");

/** Ports and hosts the stack publishes; overridable so two checkouts can run side by side. */
export const config = {
  apiPort: Number(process.env.E2E_API_PORT ?? 1630),
  webPort: Number(process.env.E2E_WEB_PORT ?? 1631),
  postgresPort: Number(process.env.E2E_POSTGRES_PORT ?? 1633),
  mocksPort: Number(process.env.E2E_MOCKS_PORT ?? 1634),
};

/**
 * Docker on a developer Mac is often OrbStack, whose CLI and socket are not on a non-login
 * shell's PATH. Fall back to them when the docker on PATH cannot reach a daemon.
 */
export function dockerEnv(): NodeJS.ProcessEnv {
  const env = { ...process.env };
  const orbBin = join(homedir(), ".orbstack", "bin");
  const orbSock = join(homedir(), ".orbstack", "run", "docker.sock");
  const reachable = spawnSync("docker", ["info", "--format", "{{.ServerVersion}}"], { env, stdio: "ignore" });
  if (reachable.status !== 0 && existsSync(orbSock)) {
    env.PATH = `${orbBin}:${env.PATH ?? ""}`;
    env.DOCKER_HOST ??= `unix://${orbSock}`;
  }
  return env;
}

const env = dockerEnv();

export function run(cmd: string, args: string[], opts: SpawnOptions = {}): Promise<number> {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(cmd, args, { stdio: "inherit", env, cwd: REPO_ROOT, ...opts });
    child.on("error", reject);
    child.on("exit", (code, signal) => resolvePromise(code ?? (signal ? 1 : 0)));
  });
}

export async function mustRun(cmd: string, args: string[], opts: SpawnOptions = {}): Promise<void> {
  const code = await run(cmd, args, opts);
  if (code !== 0) throw new Error(`${cmd} ${args.join(" ")} exited with ${code}`);
}

export function capture(cmd: string, args: string[], opts: { cwd?: string } = {}): string {
  const result = spawnSync(cmd, args, { env, cwd: opts.cwd ?? REPO_ROOT, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0) throw new Error(`${cmd} ${args.join(" ")} failed: ${result.stderr}`);
  return result.stdout;
}

export function compose(project: string, args: string[], extraEnv: NodeJS.ProcessEnv = {}): Promise<number> {
  return run("docker", ["compose", "-p", project, "-f", join(E2E_DIR, "docker-compose.yml"), ...args], {
    env: { ...env, ...extraEnv },
  });
}

/**
 * A content hash of a set of repo paths: the committed tree ids from HEAD, plus the bytes of
 * every modified or untracked file under them, so an uncommitted edit changes the hash too.
 * Excluded subtrees are pathspec exclusions (`:!src/Web`).
 */
export function inputsHash(paths: string[], salt = ""): string {
  const hash = createHash("sha256").update(salt);
  const include = paths.filter((p) => !p.startsWith(":!"));
  const exclude = paths.filter((p) => p.startsWith(":!")).map((p) => p.slice(2));
  hash.update(capture("git", ["ls-tree", "-r", "-t", "HEAD", "--", ...include])
    .split("\n")
    .filter((line) => !exclude.some((ex) => line.split("\t")[1]?.startsWith(ex)))
    .join("\n"));
  const dirty = capture("git", ["status", "--porcelain=v1", "-uall", "-z", "--", ...paths]).split("\0").filter(Boolean);
  for (const entry of dirty) {
    const file = entry.slice(3);
    const full = join(REPO_ROOT, file);
    hash.update(entry);
    if (existsSync(full) && statSync(full).isFile()) hash.update(readFileSync(full));
  }
  return hash.digest("hex").slice(0, 16);
}

export function imageExists(ref: string): boolean {
  return spawnSync("docker", ["image", "inspect", ref], { env, stdio: "ignore" }).status === 0;
}

const API_INPUTS = ["src", ":!src/Web", ":!src/Portal", ":!src/Desktop", "Directory.Packages.props", "global.json"];
const WEB_INPUTS = ["src/Web", "Dockerfile.web"];
const GENERATED_CLIENT = join(REPO_ROOT, "src/Web/packages/app/src/lib/api");
const CLIENT_STAMP = join(E2E_DIR, ".state", "client-generated-from");

export interface Images {
  api: string;
  web: string;
}

function dockerArch(): "amd64" | "arm64" {
  return capture("docker", ["version", "--format", "{{.Server.Arch}}"]).trim() === "arm64" ? "arm64" : "amd64";
}

/**
 * The API image the release pipeline publishes, built the same way: the SDK container build
 * (`dotnet publish -p:PublishProfile=DefaultContainer`), for the daemon's own architecture only.
 */
async function buildApi(ref: string) {
  const [repository, tag] = ref.split(":");
  const rid = dockerArch() === "arm64" ? "linux-arm64" : "linux-x64";
  await mustRun("dotnet", [
    "publish", "src/API/Nocturne.API/Nocturne.API.csproj",
    "-c", "Release",
    "-r", rid,
    "-p:PublishProfile=DefaultContainer",
    "-p:GenerateNSwagClient=false",
    `-p:ContainerRepository=${repository}`,
    `-p:ContainerImageTags=${tag}`,
    "-p:ContainerRegistry=",
    "-nologo", "-v:q", "-clp:ErrorsOnly",
  ]);
}

/**
 * The generated TypeScript client is gitignored and derived from the API, so it is regenerated
 * whenever the API inputs moved since the last generation, and then hashed by content: an API
 * change that leaves the client byte-identical does not rebuild the web image.
 */
async function ensureClient(apiHash: string): Promise<string> {
  const generated = join(GENERATED_CLIENT, "generated");
  const stamp = existsSync(CLIENT_STAMP) ? readFileSync(CLIENT_STAMP, "utf8").trim() : "";
  if (stamp !== apiHash || !existsSync(generated)) {
    console.log("[e2e] regenerating the API client for the web image");
    await mustRun("dotnet", [
      "build", "src/API/Nocturne.API/Nocturne.API.csproj",
      "-c", "Release", "-p:GenerateNSwagClient=true", "-nologo", "-v:q", "-clp:ErrorsOnly",
    ]);
    mkdirSync(dirname(CLIENT_STAMP), { recursive: true });
    writeFileSync(CLIENT_STAMP, apiHash);
  }
  const hash = createHash("sha256");
  const files = [join(GENERATED_CLIENT, "api-client.generated.ts"), ...listFiles(generated)].sort();
  for (const file of files) {
    if (!existsSync(file)) continue;
    hash.update(file.slice(REPO_ROOT.length));
    hash.update(readFileSync(file));
  }
  return hash.digest("hex").slice(0, 16);
}

function listFiles(dir: string): string[] {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true, recursive: true })
    .filter((d) => d.isFile())
    .map((d) => join(d.parentPath, d.name));
}

/**
 * The web image from Dockerfile.web, which bakes in the generated client. E2E_BUILDX_CACHE
 * (e.g. `type=gha,scope=e2e-web`) adds a buildx layer cache, which CI uses.
 */
async function buildWeb(ref: string) {
  const cache = process.env.E2E_BUILDX_CACHE;
  const cacheArgs = cache ? ["--cache-from", cache, "--cache-to", `${cache},mode=max`] : [];
  await mustRun("docker", ["buildx", "build", "--load", "-f", "Dockerfile.web", "-t", ref, ...cacheArgs, "."]);
}

async function ensureImage(name: keyof Images, ref: string, build: (ref: string) => Promise<void>, force: boolean) {
  if (!force && imageExists(ref)) {
    console.log(`[e2e] ${ref} is current, not rebuilding`);
  } else {
    console.log(`[e2e] building ${ref}`);
    const started = Date.now();
    await build(ref);
    console.log(`[e2e] built ${ref} in ${Math.round((Date.now() - started) / 1000)}s`);
  }
  await mustRun("docker", ["tag", ref, `nocturne-${name}:e2e`]);
  pruneStale(name, ref);
}

/**
 * Builds whichever image is missing for the current inputs and points the stable `:e2e` tags at
 * the result. With nothing changed this costs a few `git` calls and two `docker image inspect`s.
 */
export async function ensureImages(opts: { force?: boolean; only?: keyof Images } = {}): Promise<Images> {
  const apiHash = inputsHash(API_INPUTS);
  if (opts.only !== "web") await ensureImage("api", `nocturne-api:e2e-${apiHash}`, buildApi, !!opts.force);
  if (opts.only !== "api") {
    const webHash = inputsHash(WEB_INPUTS, await ensureClient(apiHash));
    await ensureImage("web", `nocturne-web:e2e-${webHash}`, buildWeb, !!opts.force);
  }
  return { api: "nocturne-api:e2e", web: "nocturne-web:e2e" };
}

/** Drops earlier content-tagged builds so each rebuild does not leave a GB behind. */
function pruneStale(name: keyof Images, keep: string) {
  const tags = capture("docker", ["image", "ls", `nocturne-${name}`, "--format", "{{.Repository}}:{{.Tag}}"])
    .split("\n")
    .filter((t) => t.startsWith(`nocturne-${name}:e2e-`) && t !== keep);
  if (tags.length > 0) spawnSync("docker", ["image", "rm", ...tags], { env, stdio: "ignore" });
}

export async function waitFor(url: string, timeoutMs: number, init?: RequestInit): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let last = "";
  while (Date.now() < deadline) {
    try {
      const res = await fetch(url, init);
      if (res.ok) return;
      last = `HTTP ${res.status}`;
    } catch (err) {
      last = String(err);
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`${url} not ready after ${timeoutMs}ms (${last})`);
}
