import type { WasmModule } from './engine-host';

/**
 * A glob rather than a static import: `src/wasm/` is gitignored, and a
 * static import fails Vite's transform when the file is absent (same shape
 * as `engine-host.ts`). Empty in that case, so the list degrades to `[]`.
 */
const wasmGlue = import.meta.glob('../wasm/nocturne_watercolour.js') as Record<string, () => Promise<WasmModule>>;

let cached: Promise<string[]> | undefined;

/** Every catalogue artwork id the wasm crate knows, loaded lazily and cached. */
export function catalogueIds(): Promise<string[]> {
  cached ??= (async () => {
    const loader = Object.values(wasmGlue)[0];
    if (!loader) return [];
    const module = await loader();
    await module.default();
    return module.catalogueIds();
  })();
  return cached;
}