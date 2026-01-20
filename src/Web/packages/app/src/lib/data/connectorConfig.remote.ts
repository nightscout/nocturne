/**
 * Remote functions for connector configuration management
 */
import { getRequestEvent, query, command } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import type {
	ConnectorConfigurationResponse,
	ConnectorStatusInfo,
} from '$lib/api/generated/nocturne-api-client';

/**
 * JSON Schema types for connector configuration
 */
export interface JsonSchemaProperty {
	type: string;
	title?: string;
	description?: string;
	default?: unknown;
	enum?: string[];
	minimum?: number;
	maximum?: number;
	minLength?: number;
	maxLength?: number;
	pattern?: string;
	format?: string;
}

export interface JsonSchema {
	$schema?: string;
	type: string;
	title?: string;
	description?: string;
	properties: Record<string, JsonSchemaProperty>;
	required?: string[];
	categories?: Record<string, string[]>;
	secrets?: string[];
}

/**
 * Get all connector configuration statuses
 */
export const getAllConnectorStatus = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.configuration.getAllConnectorStatus();
	} catch (err) {
		console.error('Error loading connector statuses:', err);
		throw error(500, 'Failed to load connector statuses');
	}
});

/**
 * Get configuration for a specific connector
 */
export const getConnectorConfiguration = query(z.string(), async (connectorName) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.configuration.getConfiguration(connectorName);
	} catch (err: unknown) {
		// 404 is expected when no config exists yet
		if (err && typeof err === 'object' && 'status' in err && err.status === 404) {
			return null;
		}
		console.error('Error loading connector configuration:', err);
		throw error(500, 'Failed to load connector configuration');
	}
});

/**
 * Get JSON schema for a connector's configuration
 */
export const getConnectorSchema = query(z.string(), async (connectorName) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.configuration.getSchema(connectorName);
		// The API returns JsonDocument, extract the actual schema
		const schema = result.rootElement as JsonSchema;

		// Handle case where schema is empty (connector type not found)
		if (!schema || !schema.properties || Object.keys(schema.properties).length === 0) {
			// Return a minimal schema indicating the connector is not configurable at runtime
			return {
				type: 'object',
				title: connectorName,
				description:
					'This connector does not support runtime configuration. Configure via environment variables.',
				properties: {},
				required: [],
				categories: {},
				secrets: [],
			} as JsonSchema;
		}

		return schema;
	} catch (err) {
		console.error('Error loading connector schema:', err);
		throw error(500, `Failed to load connector schema for ${connectorName}`);
	}
});

/**
 * Save connector configuration
 */
export const saveConnectorConfiguration = command(
	z.object({
		connectorName: z.string(),
		configuration: z.record(z.unknown()),
	}),
	async ({ connectorName, configuration }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.configuration.saveConfiguration(connectorName, {
				rootElement: configuration,
			});
			await getAllConnectorStatus().refresh();
			return {
				success: true,
				data: result,
			};
		} catch (err) {
			console.error('Error saving connector configuration:', err);
			return {
				success: false,
				error: err instanceof Error ? err.message : 'Failed to save configuration',
			};
		}
	}
);

/**
 * Save connector secrets
 */
export const saveConnectorSecrets = command(
	z.object({
		connectorName: z.string(),
		secrets: z.record(z.string()),
	}),
	async ({ connectorName, secrets }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.configuration.saveSecrets(connectorName, secrets);
			await getAllConnectorStatus().refresh();
			return { success: true };
		} catch (err) {
			console.error('Error saving connector secrets:', err);
			return {
				success: false,
				error: err instanceof Error ? err.message : 'Failed to save secrets',
			};
		}
	}
);

/**
 * Set connector active state (enable/disable)
 */
export const setConnectorActive = command(
	z.object({
		connectorName: z.string(),
		isActive: z.boolean(),
	}),
	async ({ connectorName, isActive }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.configuration.setActive(connectorName, { isActive });
			await getAllConnectorStatus().refresh();
			return { success: true };
		} catch (err) {
			console.error('Error setting connector active state:', err);
			return {
				success: false,
				error: err instanceof Error ? err.message : 'Failed to update connector state',
			};
		}
	}
);

/**
 * Delete connector configuration
 */
export const deleteConnectorConfiguration = command(z.string(), async (connectorName) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.configuration.deleteConfiguration(connectorName);
		await getAllConnectorStatus().refresh();
		return { success: true };
	} catch (err) {
		console.error('Error deleting connector configuration:', err);
		return {
			success: false,
			error: err instanceof Error ? err.message : 'Failed to delete configuration',
		};
	}
});
