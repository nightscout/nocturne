/**
 * Re-encodes the baked PNGs as WebP and removes the PNGs.
 *
 * The bake writes PNG because that is what the Rust exporter produces. These
 * marks are soft alpha washes, which PNG stores badly: the eight files a
 * paint-drop surface can draw came to 260 KB, against 76 KB as WebP.
 *
 * Quality 82 with `alphaQuality: 100`, which keeps libwebp's alpha plane
 * lossless. Alpha IS the artwork here.
 *
 * Measured against the PNGs, the silhouette moves by at most 1/255 and the
 * pigment colour by about 1/255, under marks drawn at a third of full opacity.
 * Lossless throughout was 61 % of PNG rather than 29 %, which is not worth an
 * invisible difference.
 *
 * Run by `pnpm bake` after the catalogue is written. Safe to re-run: a PNG
 * with an up-to-date WebP beside it is skipped.
 */
import { readdir, readFile, stat, unlink, writeFile } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

const ASSETS = fileURLToPath(new URL('../assets', import.meta.url));

export const WEBP_QUALITY = 82;
/** 100 keeps libwebp's alpha plane lossless, which is the point. */
export const WEBP_ALPHA_QUALITY = 100;

async function* pngs(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* pngs(path);
    else if (entry.name.endsWith('.png')) yield path;
  }
}

async function newerThan(a, b) {
  try {
    const [left, right] = await Promise.all([stat(a), stat(b)]);
    return left.mtimeMs > right.mtimeMs;
  } catch {
    return true;
  }
}

const dryRun = process.argv.includes('--dry-run');
let converted = 0;
let skipped = 0;
let before = 0;
let after = 0;

for await (const png of pngs(ASSETS)) {
  const webp = png.replace(/\.png$/, '.webp');
  const pngBytes = (await stat(png)).size;
  if (!(await newerThan(png, webp))) {
    skipped += 1;
    before += pngBytes;
    after += (await stat(webp)).size;
    continue;
  }
  const encoded = await sharp(await readFile(png))
    .webp({ quality: WEBP_QUALITY, alphaQuality: WEBP_ALPHA_QUALITY, effort: 6 })
    .toBuffer();
  before += pngBytes;
  after += encoded.length;
  converted += 1;
  if (dryRun) continue;
  await writeFile(webp, encoded);
  await unlink(png);
}

const kb = (n) => `${(n / 1024).toFixed(0)} KB`;
console.log(
  `[watercolour] ${converted} encoded, ${skipped} already current: ${kb(before)} of PNG -> ${kb(after)} of WebP ` +
    `(${((after / before) * 100).toFixed(0)} %)${dryRun ? ' [dry run, nothing written]' : ''}`,
);
if (relative(process.cwd(), ASSETS).startsWith('..') === false && converted === 0 && skipped === 0) {
  console.warn('[watercolour] no PNGs found under assets/ - has the bake run?');
}
