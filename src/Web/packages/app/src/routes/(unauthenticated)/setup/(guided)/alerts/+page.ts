import type { PageLoad } from "./$types";
import { SetupHubItemKey } from "$api";

export const load: PageLoad = () => ({ item: SetupHubItemKey.Alerts });
