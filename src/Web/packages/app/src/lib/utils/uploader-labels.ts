import type { UploaderApp } from '$lib/api/generated/nocturne-api-client';

interface UploaderLabel {
	name: string;
	description: string;
}

const UPLOADER_LABELS: Record<string, UploaderLabel> = {
	xdrip: {
		name: 'xDrip+',
		description: 'Open-source CGM app for Android',
	},
	xdrip4ios: {
		name: 'xDrip4iOS',
		description: 'Open-source CGM app for iOS',
	},
	spike: {
		name: 'Spike',
		description: 'CGM app for iOS',
	},
	juggluco: {
		name: 'Juggluco',
		description: 'Libre CGM app for Android',
	},
	glucotracker: {
		name: 'GlucoTracker',
		description: 'Glucose tracking app for Android',
	},
	loop: {
		name: 'Loop',
		description: 'Automated insulin dosing for iOS',
	},
	aaps: {
		name: 'AAPS',
		description: 'AndroidAPS automated insulin dosing',
	},
	trio: {
		name: 'Trio',
		description: 'Automated insulin dosing for iOS',
	},
	iaps: {
		name: 'iAPS',
		description: 'Automated insulin dosing for iOS',
	},
	'nightscout-uploader': {
		name: 'Nightscout Uploader',
		description: 'Android uploader for Nightscout-compatible sites',
	},
	prelude: {
		name: 'Prelude',
		description: 'CGM follower app for Android',
	},
};

/** Get the display name for an uploader app, falling back to its ID */
export function getUploaderName(uploader: UploaderApp): string {
	return UPLOADER_LABELS[uploader.id ?? '']?.name ?? uploader.id ?? 'Unknown';
}

/** Get the description for an uploader app */
export function getUploaderDescription(uploader: UploaderApp): string {
	return UPLOADER_LABELS[uploader.id ?? '']?.description ?? '';
}
