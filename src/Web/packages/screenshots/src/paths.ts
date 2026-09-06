import { fileURLToPath } from 'node:url';

export const imagesDir = fileURLToPath(new URL('../images', import.meta.url));
export const manifestPath = fileURLToPath(new URL('../manifest.json', import.meta.url));
export const docsContentDir = fileURLToPath(new URL('../../portal/src/content', import.meta.url));
/** src/Web/packages/screenshots/src -> five levels up. */
export const repoRoot = fileURLToPath(new URL('../../../../../', import.meta.url));
