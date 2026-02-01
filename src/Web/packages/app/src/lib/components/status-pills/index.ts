// Status Pills Components
export { default as StatusPill } from './StatusPill.svelte';
export { default as IOBPill } from './IOBPill.svelte';
export { default as COBPill } from './COBPill.svelte';
export { default as BasalPill } from './BasalPill.svelte';
export { default as LoopPill } from './LoopPill.svelte';
export { default as StatusPillBar } from './StatusPillBar.svelte';
export { default as TrackerPill } from './TrackerPill.svelte';
export { default as TrackerPillBar } from './TrackerPillBar.svelte';

// Re-export types
// Note: CAGEPillData and SAGEPillData are deprecated - use TrackerPill system instead
export type {
	AlertLevel,
	PillInfoItem,
	PillDisplayProps,
	COBPillData,
	IOBPillData,
	BasalPillData,
	LoopPillData,
	LoopStatusCode,
	StatusPillsConfig,
	PillType
} from '$lib/types/status-pills';

export { DEFAULT_PILLS_CONFIG } from '$lib/types/status-pills';
