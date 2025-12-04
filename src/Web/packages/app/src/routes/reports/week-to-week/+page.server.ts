import { error } from "@sveltejs/kit";
import type { ServerLoadEvent } from "@sveltejs/kit";

export const load = async ({ parent, url }: ServerLoadEvent) => {
  // Re-use data fetched in the reports layout
  const parentData = await parent();

  if (!parentData || !parentData.entries) {
    throw error(500, "Entries data unavailable for Week to Week report");
  }

  // Read week offset from URL parameter
  const weekOffsetParam = url.searchParams.get("week");
  const weekOffset = weekOffsetParam ? parseInt(weekOffsetParam) : 0;

  return {
    entries: parentData.entries,
    treatments: parentData.treatments,
    dateRange: parentData.dateRange,
    analysis: parentData.analysis,
    initialWeekOffset: isNaN(weekOffset) ? 0 : weekOffset,
  };
};
