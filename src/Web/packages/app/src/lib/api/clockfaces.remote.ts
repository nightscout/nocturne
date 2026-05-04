/**
 * Remote functions for clock face management Uses NSwag-generated types from
 * the backend as the source of truth
 */
import { getRequestEvent, query } from "$app/server";
import { z } from "zod";
import { error } from "@sveltejs/kit";

/**
 * Get a clock face by ID for editing (requires auth, includes name) Combines
 * list data (for name) with config data
 */
export const getByIdForEdit = query(z.string(), async (id) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;
  try {
    const [listItems, publicDto] = await Promise.all([
      apiClient.clockFaces.list(),
      apiClient.clockFaces.getById(id),
    ]);
    const listItem = listItems.find((item) => item.id === id);
    return {
      id: publicDto.id,
      name: listItem?.name ?? "My Clock Face",
      config: publicDto.config,
    };
  } catch (err) {
    console.error("Error loading clock face for edit:", err);
    throw error(500, "Failed to load clock face");
  }
});
