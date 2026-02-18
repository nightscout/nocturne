/**
 * Remote functions for treatments report page.
 * Data comes from V4 decomposed endpoints (boluses, carb intakes, BG checks, notes, device events).
 */
import { z } from 'zod';
import { getRequestEvent, form, command, query } from '$app/server';
import { invalid } from '@sveltejs/kit';
import type { Bolus, CarbIntake, BGCheck, Note, DeviceEvent } from '$lib/api';

/**
 * Input schema for date range queries (matches reports layout pattern)
 */
const DateRangeSchema = z.object({
	days: z.number().nullish(),
	from: z.string().nullish(),
	to: z.string().nullish(),
});

function calculateDateRange(input?: z.infer<typeof DateRangeSchema>) {
	let startDate: Date;
	let endDate: Date;

	if (input?.from && input?.to) {
		startDate = new Date(input.from);
		endDate = new Date(input.to);
	} else if (input?.days) {
		endDate = new Date();
		startDate = new Date(endDate);
		startDate.setDate(endDate.getDate() - (input.days - 1));
	} else {
		endDate = new Date();
		startDate = new Date(endDate);
		startDate.setDate(endDate.getDate() - 7);
	}

	startDate.setHours(0, 0, 0, 0);
	endDate.setHours(23, 59, 59, 999);

	return { startDate, endDate };
}

/**
 * Get all v4 entry types for the treatments page.
 * Fetches boluses, carb intakes, BG checks, notes, and device events in parallel.
 * Treatment summary comes from the backend via calculateTreatmentSummary.
 */
export const getTreatmentsData = query(
	DateRangeSchema.optional(),
	async (input) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		const { startDate, endDate } = calculateDateRange(input);
		const fromMs = startDate.getTime();
		const toMs = endDate.getTime();

		const [bolusResponse, carbResponse, bgCheckResponse, noteResponse, deviceEventResponse] =
			await Promise.all([
				apiClient.insulin.getBoluses(fromMs, toMs, 10000),
				apiClient.nutrition.getCarbIntakes(fromMs, toMs, 10000),
				apiClient.observations.getBGChecks(fromMs, toMs, 10000),
				apiClient.observations.getNotes(fromMs, toMs, 10000),
				apiClient.observations.getDeviceEvents(fromMs, toMs, 10000),
			]);

		const boluses = bolusResponse.data ?? [];
		const carbIntakes = carbResponse.data ?? [];
		const bgChecks = bgCheckResponse.data ?? [];
		const notes = noteResponse.data ?? [];
		const deviceEvents = deviceEventResponse.data ?? [];

		const treatmentSummary =
			boluses.length > 0 || carbIntakes.length > 0
				? await apiClient.statistics.calculateTreatmentSummary({ boluses, carbIntakes })
				: null;

		return {
			boluses,
			carbIntakes,
			bgChecks,
			notes,
			deviceEvents,
			treatmentSummary,
			dateRange: {
				from: startDate.toISOString(),
				to: endDate.toISOString(),
			},
		};
	}
);

/**
 * Delete a single entry form (v4: dispatches to the correct endpoint by kind)
 */
export const deleteEntryForm = form(
	z.object({
		entryId: z.string().min(1, 'Entry ID is required'),
		entryKind: z.enum(['bolus', 'carbs', 'bgCheck', 'note', 'deviceEvent']),
	}),
	async ({ entryId, entryKind }, issue) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			switch (entryKind) {
				case 'bolus':
					await apiClient.insulin.deleteBolus(entryId);
					break;
				case 'carbs':
					await apiClient.nutrition.deleteCarbIntake(entryId);
					break;
				case 'bgCheck':
					await apiClient.observations.deleteBGCheck(entryId);
					break;
				case 'note':
					await apiClient.observations.deleteNote(entryId);
					break;
				case 'deviceEvent':
					await apiClient.observations.deleteDeviceEvent(entryId);
					break;
			}

			return {
				success: true,
				message: 'Entry deleted successfully',
				deletedEntryId: entryId,
			};
		} catch (error) {
			console.error('Error deleting entry:', error);
			invalid(issue.entryId('Failed to delete entry. Please try again.'));
		}
	}
);

/**
 * Bulk delete entries command (v4: dispatches each item by kind)
 */
export const bulkDeleteEntries = command(
	z.array(
		z.object({
			id: z.string(),
			kind: z.enum(['bolus', 'carbs', 'bgCheck', 'note', 'deviceEvent']),
		})
	),
	async (items) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const deletedIds: string[] = [];
		const failedIds: string[] = [];

		for (const item of items) {
			try {
				switch (item.kind) {
					case 'bolus':
						await apiClient.insulin.deleteBolus(item.id);
						break;
					case 'carbs':
						await apiClient.nutrition.deleteCarbIntake(item.id);
						break;
					case 'bgCheck':
						await apiClient.observations.deleteBGCheck(item.id);
						break;
					case 'note':
						await apiClient.observations.deleteNote(item.id);
						break;
					case 'deviceEvent':
						await apiClient.observations.deleteDeviceEvent(item.id);
						break;
				}
				deletedIds.push(item.id);
			} catch (err) {
				console.error(`Error deleting ${item.kind} ${item.id}:`, err);
				failedIds.push(item.id);
			}
		}

		if (failedIds.length > 0) {
			return {
				success: false,
				message: `Failed to delete ${failedIds.length} of ${items.length} entries`,
				deletedEntryIds: deletedIds,
				failedEntryIds: failedIds,
			};
		}

		return {
			success: true,
			message: `Successfully deleted ${deletedIds.length} entr${deletedIds.length !== 1 ? 'ies' : 'y'}`,
			deletedEntryIds: deletedIds,
		};
	}
);

/**
 * Update a single entry (v4: dispatches to the correct endpoint by kind)
 */
export const updateEntry = command(
	z.object({
		kind: z.enum(['bolus', 'carbs', 'bgCheck', 'note', 'deviceEvent']),
		id: z.string().min(1),
		data: z.record(z.string(), z.unknown()),
	}),
	async ({ kind, id, data }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		switch (kind) {
			case 'bolus':
				return await apiClient.insulin.updateBolus(id, data as Bolus);
			case 'carbs':
				return await apiClient.nutrition.updateCarbIntake(id, data as CarbIntake);
			case 'bgCheck':
				return await apiClient.observations.updateBGCheck(id, data as BGCheck);
			case 'note':
				return await apiClient.observations.updateNote(id, data as Note);
			case 'deviceEvent':
				return await apiClient.observations.updateDeviceEvent(id, data as DeviceEvent);
		}
	}
);
