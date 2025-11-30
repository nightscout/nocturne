import type { PageServerLoad } from "./$types";
import { error } from "@sveltejs/kit";

export const load: PageServerLoad = async ({ parent }) => {
  // Leverage data already fetched by the reports layout (+layout.server.ts)
  const parentData = await parent();

  if (!parentData || !parentData.analysis) {
    throw error(500, "Analysis data promise unavailable");
  }

  // You can compute additional derived metrics here if needed

  return {
    ...parentData
  };
};