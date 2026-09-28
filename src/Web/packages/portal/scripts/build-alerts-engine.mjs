// Compiles the alert engine (crates/nocturne-alerts-ffi, `wasm` feature) to WebAssembly for the
// docs simulator, so the docs run the engine the server runs rather than a copy of its semantics.
// Output lands in static/alerts-engine/, loaded by the browser at runtime.
//
// `--optional` (dev) warns and carries on without a Rust toolchain; the simulator then says the
// engine is unavailable. A production build fails instead, so a deploy never ships a docs page
// whose demo cannot run.

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const crates = resolve(here, "../../../../../crates");
const outDir = resolve(here, "../static/alerts-engine");
const optional = process.argv.includes("--optional");
const exe = process.platform === "win32" ? ".exe" : "";

function run(command, args, cwd = crates) {
	execFileSync(command, args, { cwd, stdio: "inherit" });
}

// The CLI must be exactly the version the crate links, or it refuses the module.
function wasmBindgenVersion() {
	const lock = readFileSync(join(crates, "Cargo.lock"), "utf8");
	const match = lock.match(/name = "wasm-bindgen"\r?\nversion = "([^"]+)"/);
	if (!match) throw new Error("wasm-bindgen is not in crates/Cargo.lock");
	return match[1];
}

function wasmBindgenCli(version) {
	const root = join(crates, "target", "tools", `wasm-bindgen-${version}`);
	const cli = join(root, "bin", `wasm-bindgen${exe}`);
	if (!existsSync(cli)) {
		console.log(`[alerts-engine] installing wasm-bindgen-cli ${version}`);
		run("cargo", ["install", "wasm-bindgen-cli", "--version", version, "--locked", "--root", root]);
	}
	return cli;
}

try {
	const cli = wasmBindgenCli(wasmBindgenVersion());
	run("cargo", [
		"build",
		"--release",
		"--target",
		"wasm32-unknown-unknown",
		"-p",
		"nocturne-alerts-ffi",
		"--features",
		"wasm",
	]);

	rmSync(outDir, { recursive: true, force: true });
	mkdirSync(outDir, { recursive: true });
	run(cli, [
		join(crates, "target", "wasm32-unknown-unknown", "release", "nocturne_alerts.wasm"),
		"--target",
		"web",
		"--no-typescript",
		"--out-dir",
		outDir,
		"--out-name",
		"nocturne_alerts",
	]);
	console.log("[alerts-engine] built static/alerts-engine/");
} catch (error) {
	if (!optional) throw error;
	console.warn(
		`[alerts-engine] skipped (${error instanceof Error ? error.message : error}); ` +
			"the docs simulator will report the engine as unavailable. Install Rust with the " +
			"wasm32-unknown-unknown target to run it.",
	);
}
