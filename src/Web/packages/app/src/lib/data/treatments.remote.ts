import { getRequestEvent, query, command } from "$app/server";
import type { Treatment } from "$lib/api";
import { TreatmentSchema } from "$lib/api/generated/schemas";
import { z } from "zod";
import { TREATMENT_CATEGORIES } from "$lib/constants/treatment-categories";

// Schema for fetching treatments with pagination and filtering
const treatmentsQuerySchema = z.object({
  dateRange: z.object({
    from: z.date().optional(),
    to: z.date().optional(),
  }),
  category: z.enum(["all", "bolus", "basal", "carbs", "device", "notes"]).optional(),
  eventTypes: z.array(z.string()).optional(),
  page: z.number().optional().default(0),
  pageSize: z.number().optional().default(100),
});

/**
 * Get all treatments within a date range with optional filtering
 * Uses pagination for large datasets
 */
export const getTreatments = query(treatmentsQuerySchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const { from = new Date(), to = new Date() } = props.dateRange;
  if (!from || !to) throw new Error("Invalid date range");

  // Build the find query for the API
  const treatmentsQuery = JSON.stringify({
    created_at: {
      $gte: from.toISOString(),
      $lte: to.toISOString(),
    },
  });

  // Fetch treatments with pagination using v4 endpoint
  const treatments = await apiClient.treatments.getTreatments(
    undefined,
    props.pageSize,
    props.page * props.pageSize,
    treatmentsQuery
  );

  // Apply category filter if specified
  let filtered = treatments;
  if (props.category && props.category !== "all") {
    const categoryConfig = TREATMENT_CATEGORIES[props.category];
    if (categoryConfig) {
      const eventTypes = categoryConfig.eventTypes as readonly string[];
      filtered = treatments.filter((t) =>
        eventTypes.includes(t.eventType || "")
      );
    }
  }

  // Apply event type filter if specified
  if (props.eventTypes && props.eventTypes.length > 0) {
    filtered = filtered.filter((t) =>
      props.eventTypes!.includes(t.eventType || "")
    );
  }

  return filtered;
});

/**
 * Get all treatments for a date range (paginated fetch for large datasets)
 */
export const getAllTreatments = query(
  z.object({
    dateRange: z.object({
      from: z.date(),
      to: z.date(),
    }),
  }),
  async (props) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    const { from, to } = props.dateRange;
    const treatmentsQuery = JSON.stringify({
      created_at: {
        $gte: from.toISOString(),
        $lte: to.toISOString(),
      },
    });

    const pageSize = 1000;
    let allTreatments: Treatment[] = [];
    let offset = 0;
    let hasMore = true;

    while (hasMore) {
      const batch = await apiClient.treatments.getTreatments(
        undefined,
        pageSize,
        offset,
        treatmentsQuery
      );
      allTreatments = allTreatments.concat(batch);

      if (batch.length < pageSize) {
        hasMore = false;
      } else {
        offset += pageSize;
      }

      // Safety limit
      if (offset >= 50000) {
        console.warn("Treatment fetch reached safety limit of 50,000 records");
        hasMore = false;
      }
    }

    return allTreatments;
  }
);

/**
 * Get treatment statistics by category
 */
export const getTreatmentStats = query(
  z.object({
    dateRange: z.object({
      from: z.date(),
      to: z.date(),
    }),
  }),
  async (props) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    const { from, to } = props.dateRange;
    const treatmentsQuery = JSON.stringify({
      created_at: {
        $gte: from.toISOString(),
        $lte: to.toISOString(),
      },
    });

    // Get all treatments for stats using v4 endpoint
    const treatments = await apiClient.treatments.getTreatments(undefined, 10000, 0, treatmentsQuery);

    // Calculate category counts
    const categoryCounts: Record<string, number> = {};
    const eventTypeCounts: Record<string, number> = {};
    let totalInsulin = 0;
    let totalCarbs = 0;
    let bolusCount = 0;
    let carbEntryCount = 0;

    for (const treatment of treatments) {
      const eventType = treatment.eventType || "<none>";
      eventTypeCounts[eventType] = (eventTypeCounts[eventType] || 0) + 1;

      // Count by category
      for (const [categoryId, category] of Object.entries(TREATMENT_CATEGORIES)) {
        if ((category.eventTypes as readonly string[]).includes(eventType)) {
          categoryCounts[categoryId] = (categoryCounts[categoryId] || 0) + 1;
          break;
        }
      }

      // Aggregate insulin and carbs
      if (treatment.insulin && treatment.insulin > 0) {
        totalInsulin += treatment.insulin;
        bolusCount++;
      }
      if (treatment.carbs && treatment.carbs > 0) {
        totalCarbs += treatment.carbs;
        carbEntryCount++;
      }
    }

    return {
      total: treatments.length,
      categoryCounts,
      eventTypeCounts,
      totals: {
        insulin: totalInsulin,
        carbs: totalCarbs,
        bolusCount,
        carbEntryCount,
      },
      averages: {
        insulinPerBolus: bolusCount > 0 ? totalInsulin / bolusCount : 0,
        carbsPerEntry: carbEntryCount > 0 ? totalCarbs / carbEntryCount : 0,
      },
    };
  }
);

// Note: Mutations (update, delete) are handled via SvelteKit form actions in +page.server.ts
// This file only contains query functions for fetching data

/**
 * Create a new treatment
 */
export const createTreatment = command(TreatmentSchema, async (treatment) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;
  return apiClient.treatments.createTreatment(treatment as Treatment);
});

/**
 * Update an existing treatment
 */
export const updateTreatment = command(TreatmentSchema, async (treatment) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;
  const t = treatment as Treatment;
  if (!t._id) throw new Error("Treatment ID required for update");
  return apiClient.treatments.updateTreatment(t._id, t);
});

/**
 * Delete a treatment
 */
export const deleteTreatment = command(z.string(), async (id) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;
  return apiClient.treatments.deleteTreatment(id);
});
