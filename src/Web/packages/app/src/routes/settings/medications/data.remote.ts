/**
 * Remote functions for injectable medications management
 */
import { z } from 'zod';
import { getRequestEvent, query, command } from '$app/server';
import { error } from '@sveltejs/kit';
import type { InjectableMedication, InjectableDose, InjectableMedicationPreset } from '$lib/api';
import { getActiveMedications, getRecentDoses } from '$lib/data/medications.remote';

/**
 * Get preset medication templates (static reference data)
 */
export const getPresets = query(z.void(), async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const presets = await apiClient.injectableMedications.getPresets();
		return (presets ?? []) as InjectableMedicationPreset[];
	} catch (err) {
		console.error('Error loading medication presets:', err);
		throw error(500, 'Failed to load medication presets');
	}
});

/**
 * Get all injectable medications with optional archived filter
 */
export const getMedications = query(
	z.boolean().optional(), // includeArchived
	async (includeArchived) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const medications = await apiClient.injectableMedications.getAll(includeArchived ?? false);
			return (medications ?? []) as InjectableMedication[];
		} catch (err) {
			console.error('Error loading medications:', err);
			throw error(500, 'Failed to load medications');
		}
	}
);

/**
 * Get injectable doses with optional date range and medication filter
 */
export const getMedicationDoses = query(
	z.object({
		fromMills: z.number().optional(),
		toMills: z.number().optional(),
		medicationId: z.string().optional(),
	}),
	async ({ fromMills, toMills, medicationId }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const doses = await apiClient.injectableDoses.getAll(
				fromMills ?? undefined,
				toMills ?? undefined,
				medicationId ?? undefined
			);
			return (doses ?? []) as InjectableDose[];
		} catch (err) {
			console.error('Error loading medication doses:', err);
			throw error(500, 'Failed to load medication doses');
		}
	}
);

/**
 * Create a new custom medication
 */
export const createMedication = command(
	z.object({
		name: z.string(),
		category: z.string(),
		concentration: z.number().optional(),
		unitType: z.string().optional(),
		dia: z.number().optional(),
		onset: z.number().optional(),
		peak: z.number().optional(),
		duration: z.number().optional(),
		defaultDose: z.number().optional(),
	}),
	async (medicationData) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const medication: InjectableMedication = {
			name: medicationData.name,
			category: medicationData.category as any,
			concentration: medicationData.concentration,
			unitType: (medicationData.unitType as any) ?? 'Units',
			dia: medicationData.dia,
			onset: medicationData.onset,
			peak: medicationData.peak,
			duration: medicationData.duration,
			defaultDose: medicationData.defaultDose,
		};

		const result = await apiClient.injectableMedications.create(medication);

		await getMedications(undefined).refresh();
		await getMedications(true).refresh();
		await getActiveMedications().refresh();

		return {
			message: 'Medication created successfully',
			medication: result,
		};
	}
);

/**
 * Update an existing medication
 */
export const updateMedication = command(
	z.object({
		id: z.string(),
		medication: z.any(),
	}),
	async ({ id, medication }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const result = await apiClient.injectableMedications.update(id, medication);

		await getMedications(undefined).refresh();
		await getMedications(true).refresh();
		await getActiveMedications().refresh();

		return {
			message: 'Medication updated successfully',
			medication: result,
		};
	}
);

/**
 * Archive a medication (soft delete)
 */
export const archiveMedication = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	await apiClient.injectableMedications.archive(id);

	await getMedications(undefined).refresh();
	await getMedications(true).refresh();
	await getActiveMedications().refresh();

	return {
		message: 'Medication archived successfully',
		archivedId: id,
	};
});

/**
 * Log a new injectable dose
 */
export const logDose = command(
	z.object({
		injectableMedicationId: z.string(),
		units: z.number(),
		timestamp: z.number(),
		injectionSite: z.string().optional(),
		notes: z.string().optional(),
	}),
	async (doseData) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const dose: InjectableDose = {
			injectableMedicationId: doseData.injectableMedicationId,
			units: doseData.units,
			timestamp: doseData.timestamp,
			injectionSite: doseData.injectionSite as any,
			notes: doseData.notes,
		};

		const result = await apiClient.injectableDoses.create(dose);

		await getMedicationDoses({}).refresh();
		await getRecentDoses().refresh();

		return {
			message: 'Dose logged successfully',
			dose: result,
		};
	}
);

/**
 * Update an existing dose
 */
export const updateDose = command(
	z.object({
		id: z.string(),
		dose: z.any(),
	}),
	async ({ id, dose }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const result = await apiClient.injectableDoses.update(id, dose);

		await getMedicationDoses({}).refresh();
		await getRecentDoses().refresh();

		return {
			message: 'Dose updated successfully',
			dose: result,
		};
	}
);

/**
 * Delete a dose
 */
export const deleteDose = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	await apiClient.injectableDoses.delete(id);

	await getMedicationDoses({}).refresh();
	await getRecentDoses().refresh();

	return {
		message: 'Dose deleted successfully',
		deletedId: id,
	};
});
