// Runs the catalogue bake from the crates directory so rustup finds crates/rust-toolchain.toml,
// which it resolves from the working directory and not from --manifest-path.
import { execFileSync } from 'node:child_process';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const pkgDir = resolve(here, '..');
const cratesDir = resolve(pkgDir, '../../../../crates');
const manifest = resolve(cratesDir, 'Cargo.toml');

const args = [
  'run', '-p', 'nocturne-watercolour-wasm', '--example', 'bake_catalogue', '--release',
  '--manifest-path', manifest,
  '--', resolve(pkgDir, 'assets'), resolve(here, 'bake-manifest.json'),
];
console.log(`> cargo ${args.join(' ')}`);
execFileSync('cargo', args, { cwd: cratesDir, stdio: 'inherit', shell: process.platform === 'win32' });
