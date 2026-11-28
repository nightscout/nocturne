import type { PageLoad } from "./$types";
import { demoSubjectAccess } from "#lib/authorization/demo-subject.js";
import { getTeam } from "#lib/api/generated/teams.generated.remote.js";

export const load: PageLoad = async ({ params, parent }) => {
  if (!demoSubjectAccess(await parent()).canReadTeams) return;
  await getTeam(params.teamId);
};
