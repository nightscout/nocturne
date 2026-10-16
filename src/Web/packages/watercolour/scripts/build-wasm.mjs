// Builds the wasm adapter and its JS glue into src/wasm (gitignored), the way the
// API build produces the NSwag client: a fresh clone has no bindings until this runs.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, statSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const pkgDir = resolve(here, '..');
const cratesDir = resolve(pkgDir, '../../../../crates');
const outDir = resolve(pkgDir, 'src/wasm');
const profile = 'wasm-release';
const wasmPath = resolve(cratesDir, `target/wasm32-unknown-unknown/${profile}/nocturne_watercolour_wasm.wasm`);

const run = (cmd, args, cwd) => {
  console.log(`> ${cmd} ${args.join(' ')}`);
  execFileSync(cmd, args, { cwd, stdio: 'inherit', shell: process.platform === 'win32' });
};
// WATERCOLOUR_WASM_PROFILE=1 keeps the name section so a CPU profile attributes wasm self-time to functions.
const keepNames = process.env.WATERCOLOUR_WASM_PROFILE === '1';
const kb = (path) => `${(statSync(path).size / 1024).toFixed(1)} KB`;

run('cargo', ['build', '--profile', profile, '--target', 'wasm32-unknown-unknown', '-p', 'nocturne-watercolour-wasm'], cratesDir);
mkdirSync(outDir, { recursive: true });
run('wasm-bindgen', ['--target', 'web', '--out-dir', outDir, '--out-name', 'nocturne_watercolour', wasmPath], pkgDir);

const bgWasm = resolve(outDir, 'nocturne_watercolour_bg.wasm');
console.log(`wasm (cargo):        ${kb(wasmPath)}`);
console.log(`wasm (wasm-bindgen): ${kb(bgWasm)}`);

let hasWasmOpt = false;
try {
  execFileSync('wasm-opt', ['--version'], { stdio: 'ignore', shell: process.platform === 'win32' });
  hasWasmOpt = true;
} catch {
  console.log('wasm-opt not on PATH; skipping (install binaryen to shrink the module further)');
}
if (hasWasmOpt) {
  run('wasm-opt', ['-Os', ...(keepNames ? ['-g'] : []), '--enable-bulk-memory', '--enable-nontrapping-float-to-int', '-o', bgWasm, bgWasm], pkgDir);
  console.log(`wasm (wasm-opt -Os): ${kb(bgWasm)}`);
}
if (!existsSync(resolve(outDir, 'nocturne_watercolour.js'))) {
  throw new Error('wasm-bindgen produced no JS glue');
}
