/**
 * Remote functions for notes/tasks management
 */
import { getRequestEvent, query, command } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import {
	NoteCategory,
	type CreateNoteRequest,
	type UpdateNoteRequest,
	type ArchiveNoteRequest,
	type LinkTrackerRequest,
	type LinkStateSpanRequest,
	type FileParameter,
} from '$api';

/**
 * Get all notes with optional filtering
 */
export const getNotes = query(
	z
		.object({
			category: z.enum(NoteCategory).optional(),
			isArchived: z.boolean().optional(),
			trackerDefinitionId: z.string().optional(),
			stateSpanId: z.string().optional(),
			fromDate: z.coerce.date().optional(),
			toDate: z.coerce.date().optional(),
			limit: z.number().optional(),
			offset: z.number().optional(),
		})
		.optional(),
	async (params) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			return await apiClient.notes.getNotes(
				params?.category,
				params?.isArchived,
				params?.trackerDefinitionId,
				params?.stateSpanId,
				params?.fromDate,
				params?.toDate,
				params?.limit,
				params?.offset
			);
		} catch (err) {
			console.error('Error loading notes:', err);
			throw error(500, 'Failed to load notes');
		}
	}
);

/**
 * Get a single note by ID
 */
export const getNote = query(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.notes.getNote(id);
	} catch (err) {
		console.error('Error loading note:', err);
		throw error(500, 'Failed to load note');
	}
});

/**
 * Create a new note
 */
export const createNote = command(
	z.object({
		category: z.enum(NoteCategory),
		title: z.string().optional(),
		content: z.string(),
		occurredAt: z.coerce.date().optional(),
		checklistItems: z
			.array(
				z.object({
					text: z.string(),
					isCompleted: z.boolean().optional(),
					sortOrder: z.number().optional(),
				})
			)
			.optional(),
	}),
	async (request) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.notes.createNote(request as CreateNoteRequest);
			await getNotes(undefined).refresh();
			return result;
		} catch (err) {
			console.error('Error creating note:', err);
			throw error(500, 'Failed to create note');
		}
	}
);

/**
 * Update an existing note
 */
export const updateNote = command(
	z.object({
		id: z.string(),
		category: z.enum(NoteCategory).optional(),
		title: z.string().optional(),
		content: z.string().optional(),
		occurredAt: z.coerce.date().optional(),
		checklistItems: z
			.array(
				z.object({
					id: z.string().optional(),
					text: z.string(),
					isCompleted: z.boolean().optional(),
					completedAt: z.coerce.date().optional(),
					sortOrder: z.number().optional(),
				})
			)
			.optional(),
	}),
	async ({ id, ...request }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.notes.updateNote(id, request as UpdateNoteRequest);
			await Promise.all([getNotes(undefined).refresh(), getNote(id).refresh()]);
			return result;
		} catch (err) {
			console.error('Error updating note:', err);
			throw error(500, 'Failed to update note');
		}
	}
);

/**
 * Archive or unarchive a note
 */
export const archiveNote = command(
	z.object({
		id: z.string(),
		archive: z.boolean(),
	}),
	async ({ id, archive }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.notes.archiveNote(id, { archive } as ArchiveNoteRequest);
			await getNotes(undefined).refresh();
			return result;
		} catch (err) {
			console.error('Error archiving note:', err);
			throw error(500, 'Failed to archive note');
		}
	}
);

/**
 * Delete a note
 */
export const deleteNote = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.notes.deleteNote(id);
		await getNotes(undefined).refresh();
		return { success: true };
	} catch (err) {
		console.error('Error deleting note:', err);
		throw error(500, 'Failed to delete note');
	}
});

/**
 * Toggle a checklist item's completion status
 */
export const toggleChecklistItem = command(
	z.object({
		noteId: z.string(),
		itemId: z.string(),
	}),
	async ({ noteId, itemId }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.notes.toggleChecklistItem(noteId, itemId);
			await Promise.all([getNotes(undefined).refresh(), getNote(noteId).refresh()]);
			return result;
		} catch (err) {
			console.error('Error toggling checklist item:', err);
			throw error(500, 'Failed to toggle checklist item');
		}
	}
);

/**
 * Get attachment download URL
 * Note: This triggers a download, returning the attachment data
 */
export const getAttachment = query(
	z.object({
		noteId: z.string(),
		attachmentId: z.string(),
	}),
	async ({ noteId, attachmentId }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.notes.downloadAttachment(noteId, attachmentId);
			return { success: true };
		} catch (err) {
			console.error('Error downloading attachment:', err);
			throw error(500, 'Failed to download attachment');
		}
	}
);

/**
 * Upload an attachment to a note
 */
export const uploadAttachment = command(
	z.object({
		noteId: z.string(),
		file: z.any(), // FileParameter type from API
	}),
	async ({ noteId, file }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.notes.uploadAttachment(noteId, file as FileParameter);
			await getNote(noteId).refresh();
			return result;
		} catch (err) {
			console.error('Error uploading attachment:', err);
			throw error(500, 'Failed to upload attachment');
		}
	}
);

/**
 * Delete an attachment from a note
 */
export const deleteAttachment = command(
	z.object({
		noteId: z.string(),
		attachmentId: z.string(),
	}),
	async ({ noteId, attachmentId }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.notes.deleteAttachment(noteId, attachmentId);
			await getNote(noteId).refresh();
			return { success: true };
		} catch (err) {
			console.error('Error deleting attachment:', err);
			throw error(500, 'Failed to delete attachment');
		}
	}
);

/**
 * Link a tracker to a note
 */
export const linkTracker = command(
	z.object({
		noteId: z.string(),
		trackerDefinitionId: z.string(),
		thresholds: z
			.array(
				z.object({
					hoursOffset: z.number().optional(),
					urgency: z.string().optional(),
					description: z.string().optional(),
				})
			)
			.optional(),
	}),
	async ({ noteId, trackerDefinitionId, thresholds }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.notes.linkTracker(noteId, {
				trackerDefinitionId,
				thresholds,
			} as LinkTrackerRequest);
			await getNote(noteId).refresh();
			return result;
		} catch (err) {
			console.error('Error linking tracker:', err);
			throw error(500, 'Failed to link tracker');
		}
	}
);

/**
 * Unlink a tracker from a note
 */
export const unlinkTracker = command(
	z.object({
		noteId: z.string(),
		linkId: z.string(),
	}),
	async ({ noteId, linkId }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.notes.unlinkTracker(noteId, linkId);
			await getNote(noteId).refresh();
			return { success: true };
		} catch (err) {
			console.error('Error unlinking tracker:', err);
			throw error(500, 'Failed to unlink tracker');
		}
	}
);

/**
 * Link a state span to a note
 */
export const linkStateSpan = command(
	z.object({
		noteId: z.string(),
		stateSpanId: z.string(),
	}),
	async ({ noteId, stateSpanId }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.notes.linkStateSpan(noteId, {
				stateSpanId,
			} as LinkStateSpanRequest);
			await getNote(noteId).refresh();
			return result;
		} catch (err) {
			console.error('Error linking state span:', err);
			throw error(500, 'Failed to link state span');
		}
	}
);

/**
 * Unlink a state span from a note
 */
export const unlinkStateSpan = command(
	z.object({
		noteId: z.string(),
		linkId: z.string(),
	}),
	async ({ noteId, linkId }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.notes.unlinkStateSpan(noteId, linkId);
			await getNote(noteId).refresh();
			return { success: true };
		} catch (err) {
			console.error('Error unlinking state span:', err);
			throw error(500, 'Failed to unlink state span');
		}
	}
);
