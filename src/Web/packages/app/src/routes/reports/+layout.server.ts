import type { LayoutServerLoad } from "./$types";
import { error } from "@sveltejs/kit";

export const load: LayoutServerLoad = async ({ url, locals }) => {
  console.log("Loading reports layout...");
  // Extract date parameters from URL
  const daysParam = url.searchParams.get("days");
  const fromParam = url.searchParams.get("from");
  const toParam = url.searchParams.get("to");
  const typeParam = url.searchParams.get("type");
  const eventTypeParam = url.searchParams.get("eventType");

  // Store raw params for child routes to access
  const rawParams = {
    days: daysParam || undefined,
    from: fromParam || undefined,
    to: toParam || undefined,
    type: typeParam || undefined,
    eventType: eventTypeParam || undefined,
  };

  // Calculate date range
  let startDate: Date;
  let endDate: Date;

  if (fromParam && toParam) {
    // Use explicit date range
    startDate = new Date(fromParam);
    endDate = new Date(toParam);
  } else if (daysParam) {
    // Use explicit days parameter
    const days = parseInt(daysParam);
    endDate = new Date();
    startDate = new Date(endDate);
    startDate.setDate(endDate.getDate() - (days - 1));
  } else {
    // Default to last 24 hours
    endDate = new Date();
    startDate = new Date(endDate);
    startDate.setDate(endDate.getDate() - 1);
  }

  // Validate dates
  if (isNaN(startDate.getTime()) || isNaN(endDate.getTime())) {
    throw error(400, "Invalid date parameters provided");
  }

  // Set to full day boundaries
  startDate.setHours(0, 0, 0, 0);
  endDate.setHours(23, 59, 59, 999);

  // Build find query for entries and treatments
  const entriesQuery = `find[date][$gte]=${startDate.toISOString()}&find[date][$lte]=${endDate.toISOString()}`;
  const treatmentsQuery = `find[created_at][$gte]=${startDate.toISOString()}&find[created_at][$lte]=${endDate.toISOString()}`;

  // API has a max count of 1000 per request, so we need to paginate
  const pageSize = 1000;

  // Fetch entries and treatments sequentially to avoid DbContext threading issues
  const entries = await locals.apiClient.entries.getEntries2(entriesQuery);

  // Fetch all treatments by paginating through results
  let allTreatments: Awaited<ReturnType<typeof locals.apiClient.treatments.getTreatments2>> = [];
  let offset = 0;
  let hasMore = true;

  while (hasMore) {
    const batch = await locals.apiClient.treatments.getTreatments2(treatmentsQuery, pageSize, offset);
    allTreatments = allTreatments.concat(batch);

    // If we got fewer than pageSize, we've reached the end
    if (batch.length < pageSize) {
      hasMore = false;
    } else {
      offset += pageSize;
    }

    // Safety limit to prevent infinite loops (max 50,000 treatments)
    if (offset >= 50000) {
      console.warn("Treatment fetch reached safety limit of 50,000 records");
      hasMore = false;
    }
  }

  const treatments = allTreatments;

  console.log(`Fetched ${entries.length} entries, ${treatments.length} treatments`);
  return {
    treatments,
    entries,
    summary: locals.apiClient.statistics.getMultiPeriodStatistics(),
    // Extended analytics includes base GlucoseAnalytics plus GMI, GRI, clinical assessment, and pattern analysis
    analysis: locals.apiClient.statistics.analyzeGlucoseDataExtended({
      entries,
      treatments,
      population: 0, // Default to Type1 - can be made configurable via user settings
    }),
    /** All ISO strings */
    dateRange: {
      from: startDate.toISOString(),
      to: endDate.toISOString(),
      lastUpdated: new Date().toISOString(),
    },
    rawParams,
  };
};
