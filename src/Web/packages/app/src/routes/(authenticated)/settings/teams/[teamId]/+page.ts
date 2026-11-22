import type { PageLoad } from "./$types";
import { getTeam } from "#lib/api/generated/teams.generated.remote.js";

export const load: PageLoad = async ({ params, parent }) => {
  if ((await parent()).refusedAsDemoSubject) return;
  await getTeam(params.teamId);
};
