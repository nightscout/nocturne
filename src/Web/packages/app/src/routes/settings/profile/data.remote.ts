/**
 * Remote functions for profile management
 */
import { z } from 'zod';
import { getRequestEvent, query, command } from '$app/server';
import { error } from '@sveltejs/kit';
import type { Profile } from '$lib/api';

/**
 * Get all profiles with sorting and selection
 */
export const getProfiles = query(
	z.string().optional(), // selectedProfileId
	async (selectedProfileId) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const response = await apiClient.profile.getProfiles2();
			const profiles = (response ?? []) as Profile[];

			// Sort profiles by mills (timestamp) descending to get most recent first
			const sortedProfiles = [...profiles].sort((a, b) => {
				const millsA = a.mills ?? 0;
				const millsB = b.mills ?? 0;
				return millsB - millsA;
			});

			// Get the current/active profile (most recent)
			const currentProfile = sortedProfiles.length > 0 ? sortedProfiles[0] : null;

			// Determine selected profile based on param or default to current
			let selectedProfile: Profile | null = null;
			if (selectedProfileId) {
				selectedProfile = sortedProfiles.find((p) => p._id === selectedProfileId) ?? null;
			}
			// If no param or profile not found, default to current
			if (!selectedProfile) {
				selectedProfile = currentProfile;
			}

			return {
				profiles: sortedProfiles,
				currentProfile,
				selectedProfile,
				selectedProfileId: selectedProfile?._id ?? null,
				totalProfiles: profiles.length,
			};
		} catch (err) {
			console.error('Error loading profiles:', err);
			throw error(500, 'Failed to load profiles');
		}
	}
);

/**
 * Create a new profile
 */
export const createProfile = command(
	z.object({
		defaultProfile: z.string().optional(),
		dia: z.number().optional(),
		carbs_hr: z.number().optional(),
		timezone: z.string().optional(),
		units: z.string().optional(),
		icon: z.string().optional(),
	}),
	async (profileData) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const now = Date.now();
		const storeName = profileData.defaultProfile || 'Default';

		// Build the profile structure
		const newProfile: Profile = {
			defaultProfile: storeName,
			startDate: new Date(now).toISOString(),
			mills: now,
			created_at: new Date(now).toISOString(),
			units: profileData.units || 'mg/dL',
			store: {
				[storeName]: {
					dia: profileData.dia ?? 4,
					carbs_hr: profileData.carbs_hr ?? 20,
					delay: 0,
					timezone: profileData.timezone ?? Intl.DateTimeFormat().resolvedOptions().timeZone,
					units: profileData.units || 'mg/dL',
					basal: [{ time: '00:00', value: 1.0 }],
					carbratio: [{ time: '00:00', value: 10 }],
					sens: [{ time: '00:00', value: 50 }],
					target_low: [{ time: '00:00', value: 80 }],
					target_high: [{ time: '00:00', value: 120 }],
				},
			},
		};

		// Add icon if provided
		if (profileData.icon) {
			(newProfile as any).icon = profileData.icon;
		}

		const result = await apiClient.profile.createProfiles([newProfile]);
		const createdProfile = Array.isArray(result) ? result[0] : result;

		// Refresh the profiles query
		await getProfiles(undefined).refresh();

		return {
			message: 'Profile created successfully',
			createdProfile,
		};
	}
);

/**
 * Update an existing profile
 */
export const updateProfile = command(
	z.object({
		profileId: z.string(),
		profileData: z.any(), // Full Profile object
	}),
	async ({ profileId, profileData }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const updatedProfile = await apiClient.profile.updateProfile(profileId, profileData);

		// Refresh the profiles query
		await getProfiles(undefined).refresh();

		return {
			message: 'Profile updated successfully',
			updatedProfile,
		};
	}
);

/**
 * Delete a profile
 */
export const deleteProfile = command(z.string(), async (profileId) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	await apiClient.profile.deleteProfile(profileId);

	// Refresh the profiles query
	await getProfiles(undefined).refresh();

	return {
		message: 'Profile deleted successfully',
		deletedProfileId: profileId,
	};
});
