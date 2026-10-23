import { error } from "@sveltejs/kit";
import type { PageLoad } from "./$types";
import { setupHubItemBySlug } from "$lib/setup-hub/items.svelte";

export const load: PageLoad = ({ params }) => {
  const item = setupHubItemBySlug(params.item);
  if (!item) error(404, "Not found");
  return { item };
};
