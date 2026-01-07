/**
 * Remote functions for authorization management (subjects, roles, permissions)
 */
import { getRequestEvent, query, command } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import type { Subject, Role } from '$api';

// ============================================================================
// Subjects
// ============================================================================

/**
 * Get all subjects (users, devices, services)
 */
export const getSubjects = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.authorization.getAllSubjects();
	} catch (err) {
		console.error('Error loading subjects:', err);
		throw error(500, 'Failed to load subjects');
	}
});

/**
 * Create a new subject
 */
export const createSubject = command(
	z.object({
		name: z.string(),
		roles: z.array(z.string()).optional(),
		notes: z.string().optional(),
	}),
	async (request) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const subject: Subject = {
				name: request.name,
				roles: request.roles,
				notes: request.notes,
			};
			const result = await apiClient.authorization.createSubject(subject);
			await getSubjects().refresh();
			return result;
		} catch (err) {
			console.error('Error creating subject:', err);
			throw error(500, 'Failed to create subject');
		}
	}
);

/**
 * Update an existing subject
 */
export const updateSubject = command(
	z.object({
		id: z.string(),
		name: z.string(),
		roles: z.array(z.string()).optional(),
		notes: z.string().optional(),
	}),
	async (request) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const subject: Subject = {
				id: request.id,
				name: request.name,
				roles: request.roles,
				notes: request.notes,
			};
			const result = await apiClient.authorization.updateSubject(subject);
			await getSubjects().refresh();
			return result;
		} catch (err) {
			console.error('Error updating subject:', err);
			throw error(500, 'Failed to update subject');
		}
	}
);

/**
 * Delete a subject
 */
export const deleteSubject = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.authorization.deleteSubject(id);
		await getSubjects().refresh();
		return { success: true };
	} catch (err) {
		console.error('Error deleting subject:', err);
		throw error(500, 'Failed to delete subject');
	}
});

// ============================================================================
// Roles
// ============================================================================

/**
 * Get all roles
 */
export const getRoles = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.authorization.getAllRoles();
	} catch (err) {
		console.error('Error loading roles:', err);
		throw error(500, 'Failed to load roles');
	}
});

/**
 * Create a new role
 */
export const createRole = command(
	z.object({
		name: z.string(),
		permissions: z.array(z.string()).optional(),
		notes: z.string().optional(),
	}),
	async (request) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const role: Role = {
				name: request.name,
				permissions: request.permissions,
				notes: request.notes,
			};
			const result = await apiClient.authorization.createRole(role);
			await getRoles().refresh();
			return result;
		} catch (err) {
			console.error('Error creating role:', err);
			throw error(500, 'Failed to create role');
		}
	}
);

/**
 * Update an existing role
 */
export const updateRole = command(
	z.object({
		id: z.string(),
		name: z.string(),
		permissions: z.array(z.string()).optional(),
		notes: z.string().optional(),
	}),
	async (request) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const role: Role = {
				id: request.id,
				name: request.name,
				permissions: request.permissions,
				notes: request.notes,
			};
			const result = await apiClient.authorization.updateRole(role);
			await getRoles().refresh();
			return result;
		} catch (err) {
			console.error('Error updating role:', err);
			throw error(500, 'Failed to update role');
		}
	}
);

/**
 * Delete a role
 */
export const deleteRole = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.authorization.deleteRole(id);
		await getRoles().refresh();
		return { success: true };
	} catch (err) {
		console.error('Error deleting role:', err);
		throw error(500, 'Failed to delete role');
	}
});

// ============================================================================
// Permissions
// ============================================================================

/**
 * Get all known permissions
 */
export const getPermissions = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.authorization.getAllPermissions();
	} catch (err) {
		console.error('Error loading permissions:', err);
		throw error(500, 'Failed to load permissions');
	}
});

/**
 * Get permission trie structure
 */
export const getPermissionTrie = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.authorization.getPermissionTrie();
	} catch (err) {
		console.error('Error loading permission trie:', err);
		throw error(500, 'Failed to load permission trie');
	}
});
