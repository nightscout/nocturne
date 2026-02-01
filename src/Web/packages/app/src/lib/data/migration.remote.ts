/**
 * Remote functions for data migration from Nightscout
 */
import { getRequestEvent, query, command } from "$app/server";
import { z } from "zod";
import { error } from "@sveltejs/kit";
import type { TestMigrationConnectionRequest, StartMigrationRequest } from "$lib/api";
import { TestMigrationConnectionRequestSchema, StartMigrationRequestSchema } from "$lib/api/generated/schemas";

/**
 * Test a migration source connection
 */
export const testMigrationConnection = command(
  TestMigrationConnectionRequestSchema,
  async (request) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      return await apiClient.migration.testConnection(request as TestMigrationConnectionRequest);
    } catch (err) {
      console.error("Error testing migration connection:", err);
      throw error(500, "Failed to test migration connection");
    }
  }
);

/**
 * Start a new migration job
 */
export const startMigration = command(StartMigrationRequestSchema, async (request) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    const result = await apiClient.migration.startMigration(request as StartMigrationRequest);
    // Note: query refresh is handled by the caller
    return result;
  } catch (err) {
    console.error("Error starting migration:", err);
    throw error(500, "Failed to start migration");
  }
});

/**
 * Get the status of a migration job
 */
export const getMigrationStatus = command(
  z.object({ jobId: z.string().uuid() }),
  async ({ jobId }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      return await apiClient.migration.getStatus(jobId);
    } catch (err) {
      console.error("Error getting migration status:", err);
      throw error(500, "Failed to get migration status");
    }
  }
);

/**
 * Cancel a running migration job
 */
export const cancelMigration = command(
  z.object({ jobId: z.string().uuid() }),
  async ({ jobId }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      await apiClient.migration.cancelMigration(jobId);
      // Note: query refresh is handled by the caller
    } catch (err) {
      console.error("Error cancelling migration:", err);
      throw error(500, "Failed to cancel migration");
    }
  }
);

/**
 * Get migration job history
 */
export const getMigrationHistory = query(async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    return await apiClient.migration.getHistory();
  } catch (err) {
    console.error("Error getting migration history:", err);
    throw error(500, "Failed to get migration history");
  }
});

/**
 * Get pending migration config from environment variables
 */
export const getPendingConfig = query(async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    return await apiClient.migration.getPendingConfig();
  } catch (err) {
    console.error("Error getting pending migration config:", err);
    throw error(500, "Failed to get pending migration config");
  }
});

/**
 * Get saved migration sources with last sync timestamps
 */
export const getMigrationSources = query(async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    return await apiClient.migration.getSources();
  } catch (err) {
    console.error("Error getting migration sources:", err);
    throw error(500, "Failed to get migration sources");
  }
});

