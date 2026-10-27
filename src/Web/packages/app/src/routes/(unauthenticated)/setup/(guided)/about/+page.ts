import type { PageLoad } from "./$types";
import { SetupHubItemKey } from "#lib/api/index.js";

export const load: PageLoad = () => ({ item: SetupHubItemKey.About });
