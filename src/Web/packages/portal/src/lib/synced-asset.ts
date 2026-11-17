import { asset } from "$app/paths";
import type { AssetPath } from "$app/types";

/**
 * The URL of a file the build syncs into `static/` (screenshots, videos, the alerts engine).
 * `asset()` is typed to the files present when types are generated, which these are not yet.
 */
export function syncedAsset(file: string): string {
  // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- the file is in static/ by the time this runs; see above
  return asset(file as AssetPath);
}
