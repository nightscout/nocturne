/**
 * Encodes the baked 512 px finals as AVIF beside their PNGs. Runs before
 * `to-webp.mjs`, which encodes the WebP fallback of every image and deletes
 * the PNGs.
 *
 * AVIF has no separate alpha quality, so the alpha plane is lossy with the
 * colour, and the quality is the lowest whose alpha stays within 8/255 of the
 * PNG with colour PSNR (premultiplied) of at least 42 dB, over a wash, two
 * catalogue artworks, an accent, a Lucide icon and two strips, both surfaces:
 *
 *   q70  max alpha 19/255, worst PSNR 47.1 dB
 *   q80  max alpha 14/255, worst PSNR 48.9 dB
 *   q85  max alpha 10/255, worst PSNR 50.4 dB
 *   q90  max alpha  7/255, worst PSNR 51.9 dB, mean alpha error <= 0.27/255
 *
 * 4:4:4 chroma: the washes' edges are colour as much as alpha.
 *
 * Only `final-512` is encoded. At q90 over the whole fresh bake (100 sets):
 *
 *   final-512  2,581,166 B WebP -> 1,654,278 B AVIF  (-36 %)
 *   final-128    364,364 B WebP ->   401,262 B AVIF  (+10 %)
 *   strip      2,474,280 B WebP -> 4,324,449 B AVIF  (+75 %)
 *
 * A small still and a tall strip are cheaper as WebP, whose lossless alpha
 * plane compresses well at those sizes.
 *
 * Safe to re-run: a PNG with an up-to-date AVIF beside it is skipped.
 */
import { readdir, readFile, stat, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

const ASSETS = fileURLToPath(new URL('../assets', import.meta.url));

export const AVIF_QUALITY = 90;

async function* finals(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* finals(path);
    else if (entry.name === 'final-512.png') yield path;
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

for await (const png of finals(ASSETS)) {
  const avif = png.replace(/\.png$/, '.avif');
  const pngBytes = (await stat(png)).size;
  before += pngBytes;
  if (!(await newerThan(png, avif))) {
    skipped += 1;
    after += (await stat(avif)).size;
    continue;
  }
  const encoded = await sharp(await readFile(png))
    .avif({ quality: AVIF_QUALITY, chromaSubsampling: '4:4:4', effort: 6 })
    .toBuffer();
  after += encoded.length;
  converted += 1;
  if (!dryRun) await writeFile(avif, encoded);
}

const kb = (n) => `${(n / 1024).toFixed(0)} KB`;
console.log(
  `[watercolour] ${converted} encoded, ${skipped} already current: ${kb(before)} of final-512 PNG -> ${kb(after)} of AVIF ` +
    `(${before ? ((after / before) * 100).toFixed(0) : 0} %)${dryRun ? ' [dry run, nothing written]' : ''}`,
);
if (converted === 0 && skipped === 0) {
  console.warn('[watercolour] no final-512 PNGs found under assets/ - has the bake run?');
}
