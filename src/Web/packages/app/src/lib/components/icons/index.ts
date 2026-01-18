/**
 * Semantic icon components for diabetes-related events and states
 *
 * These components wrap Lucide icons to provide semantic meaning.
 * They can be swapped out for custom icons later without changing call sites.
 *
 * NOTE: For SVG chart contexts (inside <svg>), use the raw SVG paths defined
 * in GlucoseChartCard.svelte since Lucide components render their own <svg>.
 * These components are for regular HTML contexts (modals, lists, tooltips).
 */

export type { IconProps } from './types';


// System Event Icons (by severity)
export { default as AlarmIcon } from './AlarmIcon.svelte';
export { default as HazardIcon } from './HazardIcon.svelte';
export { default as WarningIcon } from './WarningIcon.svelte';
export { default as InfoIcon } from './InfoIcon.svelte';

// Pump Mode Icons
export { default as AutomaticModeIcon } from './AutomaticModeIcon.svelte';
export { default as ManualModeIcon } from './ManualModeIcon.svelte';
export { default as BoostModeIcon } from './BoostModeIcon.svelte';
export { default as SleepModeIcon } from './SleepModeIcon.svelte';
export { default as ExerciseModeIcon } from './ExerciseModeIcon.svelte';
export { default as SuspendedModeIcon } from './SuspendedModeIcon.svelte';
export { default as LimitedModeIcon } from './LimitedModeIcon.svelte';
export { default as EaseOffModeIcon } from './EaseOffModeIcon.svelte';

// Device Event Icons
export { default as SensorIcon } from './SensorIcon.svelte';
export { default as SiteChangeIcon } from './SiteChangeIcon.svelte';
export { default as ReservoirIcon } from './ReservoirIcon.svelte';
export { default as BatteryIcon } from './BatteryIcon.svelte';
export { default as CannulaIcon } from './CannulaIcon.svelte';

// Dynamic Icon Components (select icon based on state/type)
export { default as SystemEventIcon } from './SystemEventIcon.svelte';
export { default as PumpModeIcon } from './PumpModeIcon.svelte';
export { default as DeviceEventIcon } from './DeviceEventIcon.svelte';
export { default as TrackerCategoryIcon } from './TrackerCategoryIcon.svelte';
export { default as TreatmentTypeIcon } from './TreatmentTypeIcon.svelte';
export { default as ActivityCategoryIcon } from './ActivityCategoryIcon.svelte';

// Treatment Marker Icons (for charts and legends)
export { default as BolusIcon } from './BolusIcon.svelte';
export { default as CarbsIcon } from './CarbsIcon.svelte';
