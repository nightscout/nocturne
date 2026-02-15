/**
 * Remote functions for services management
 */
import { getRequestEvent, query, command } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';

/**
 * Get services overview including active data sources, uploaders, and connectors
 */
export const getServicesOverview = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.services.getServicesOverview();
	} catch (err) {
		console.error('Error loading services overview:', err);
		throw error(500, 'Failed to load services');
	}
});

/**
 * Get uploader setup instructions
 */
export const getUploaderSetup = query(z.string(), async (uploaderId) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.services.getUploaderSetup(uploaderId);
	} catch (err) {
		console.error('Error loading uploader setup:', err);
		throw error(500, 'Failed to load setup instructions');
	}
});

/**
 * Delete demo data
 */
export const deleteDemoData = command(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.services.deleteDemoData();
		await getServicesOverview().refresh();
		return {
			success: result.success ?? false,
			entriesDeleted: result.entriesDeleted,
			error: result.error ?? undefined,
		};
	} catch (err) {
		console.error('Error deleting demo data:', err);
		return {
			success: false,
			error: err instanceof Error ? err.message : 'Failed to delete demo data',
		};
	}
});

/**
 * Delete data source data
 */
export const deleteDataSourceData = command(z.string(), async (dataSourceId) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.services.deleteDataSourceData(dataSourceId);
		await getServicesOverview().refresh();
		return {
			success: result.success ?? false,
			entriesDeleted: result.entriesDeleted,
			error: result.error ?? undefined,
		};
	} catch (err) {
		console.error('Error deleting data source:', err);
		return {
			success: false,
			error: err instanceof Error ? err.message : 'Failed to delete data',
		};
	}
});

/**
 * Delete connector data
 */
export const deleteConnectorData = command(z.string(), async (connectorId) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.services.deleteConnectorData(connectorId);
		// Refresh both services overview and connector statuses to update counts everywhere
		await Promise.all([
			getServicesOverview().refresh(),
			getConnectorStatuses().refresh(),
		]);
		const entriesDeleted = result.entriesDeleted ?? 0;
		const treatmentsDeleted = result.treatmentsDeleted ?? 0;
		const deviceStatusDeleted = result.deviceStatusDeleted ?? 0;
		return {
			success: result.success ?? false,
			entriesDeleted,
			treatmentsDeleted,
			deviceStatusDeleted,
			totalDeleted: entriesDeleted + treatmentsDeleted + deviceStatusDeleted,
			error: result.error ?? undefined,
		};
	} catch (err) {
		console.error('Error deleting connector data:', err);
		return {
			success: false,
			error: err instanceof Error ? err.message : 'Failed to delete connector data',
		};
	}
});


/**
 * Get connector health status and metrics
 */
export const getConnectorStatuses = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.connectorStatus.getStatus();
	} catch (err) {
		console.error('Error loading connector statuses:', err);
		return [];
	}
});

/**
 * Get connector capabilities
 */
export const getConnectorCapabilities = query(z.string(), async (connectorId) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.services.getConnectorCapabilities(connectorId);
	} catch (err) {
		console.error('Error loading connector capabilities:', err);
		return null;
	}
});

/**
 * Start a deduplication job
 */
export const startDeduplicationJob = command(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.deduplication.startDeduplicationJob();
		return {
			success: true,
			jobId: result.jobId,
			message: result.message,
			statusUrl: result.statusUrl,
		};
	} catch (err) {
		console.error('Error starting deduplication job:', err);
		return {
			success: false,
			error: err instanceof Error ? err.message : 'Failed to start deduplication job',
		};
	}
});

/**
 * Get deduplication job status
 */
export const getDeduplicationJobStatus = query(z.string(), async (jobId) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.deduplication.getJobStatus(jobId);
	} catch (err) {
		console.error('Error getting deduplication job status:', err);
		return null;
	}
});

/**
 * Cancel a deduplication job
 */
export const cancelDeduplicationJob = command(z.string(), async (jobId) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.deduplication.cancelJob(jobId);
		return {
			success: result.cancelled ?? false,
			message: result.message,
		};
	} catch (err) {
		console.error('Error cancelling deduplication job:', err);
		return {
			success: false,
			error: err instanceof Error ? err.message : 'Failed to cancel job',
		};
	}
});
