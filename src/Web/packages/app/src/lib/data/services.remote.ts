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
