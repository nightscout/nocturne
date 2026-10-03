// Dumps the element lists for every `lucide:<name>` entry in the bake
// manifest to `scripts/lucide-icons.json`, which the Rust bake example reads
// (it cannot import npm packages). Runs before the bake so the JSON is always
// regenerated from the manifest. The vanilla `lucide` package exports each
// icon as a PascalCase named export holding its `IconNode` element list.

import { readFileSync, writeFileSync } from 'node:fs';
import * as lucide from 'lucide';

const manifest = JSON.parse(
  readFileSync(new URL('./bake-manifest.json', import.meta.url), 'utf8'),
);

const pascal = (name) =>
  name
    .split('-')
    .map((part) => part[0].toUpperCase() + part.slice(1))
    .join('');

const icons = {};
for (const artwork of manifest.artworks) {
  const prefix = 'lucide:';
  if (!artwork.id.startsWith(prefix)) continue;
  const name = artwork.id.slice(prefix.length);
  const node = lucide[pascal(name)];
  if (!node) throw new Error(`lucide has no export ${pascal(name)} for ${artwork.id}`);
  icons[name] = node;
}

writeFileSync(
  new URL('./lucide-icons.json', import.meta.url),
  `${JSON.stringify(icons, null, 2)}\n`,
);
console.log(`wrote ${Object.keys(icons).length} lucide icons to scripts/lucide-icons.json`);