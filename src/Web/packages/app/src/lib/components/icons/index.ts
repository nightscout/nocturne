/**
 * Semantic icon components for diabetes-related events and states
 *
 * Device event and therapy icons use AAPS (AndroidAPS) SVG paths.
 * Chart marker icons (BolusIcon, CarbsIcon) use the shared triangle geometry
 * in @nocturne/charts, so they match what the chart draws.
 *
 * NOTE: For SVG chart contexts (inside <svg>), use the raw SVG paths defined
 * in GlucoseChartCard.svelte since Lucide components render their own <svg>.
 * These components are for regular HTML contexts (modals, lists, tooltips).
 */

export type { IconProps } from "@nocturne/charts/icons";

// The icons the glucose chart draws live with it in @nocturne/charts.
export {
  AlarmIcon,
  HazardIcon,
  WarningIcon,
  InfoIcon,
  AutomaticModeIcon,
  ManualModeIcon,
  BoostModeIcon,
  SleepModeIcon,
  ExerciseModeIcon,
  SuspendedModeIcon,
  LimitedModeIcon,
  EaseOffModeIcon,
  SensorIcon,
  SiteChangeIcon,
  ReservoirIcon,
  BatteryIcon,
  CannulaIcon,
  PodIcon,
  SystemEventIcon,
  PumpModeIcon,
  DeviceEventIcon,
  TrackerCategoryIcon,
  ActivityCategoryIcon,
  BolusIcon,
  CarbsIcon,
} from "@nocturne/charts/icons";

export { default as SiteRotationIcon } from "./SiteRotationIcon.svelte";

// Therapy Event Icons (AAPS SVG icons)
export { default as BgCheckIcon } from "./BgCheckIcon.svelte";
export { default as CalibrationIcon } from "./CalibrationIcon.svelte";
export { default as ExtendedBolusIcon } from "./ExtendedBolusIcon.svelte";
export { default as NoteIcon } from "./NoteIcon.svelte";
export { default as AnnouncementIcon } from "./AnnouncementIcon.svelte";
export { default as ActivityIcon } from "./ActivityIcon.svelte";
export { default as ProfileSwitchIcon } from "./ProfileSwitchIcon.svelte";
export { default as QuestionEventIcon } from "./QuestionEventIcon.svelte";
export { default as BolusEventIcon } from "./BolusEventIcon.svelte";
export { default as CarbsEventIcon } from "./CarbsEventIcon.svelte";
