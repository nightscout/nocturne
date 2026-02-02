<script lang="ts" module>
  export interface NoteThreshold {
    id?: string;
    hoursOffset: number | undefined;
    description?: string;
  }
</script>

<script lang="ts">
  import { ThresholdEditor } from "$lib/components/ui/threshold-editor";

  interface Props {
    /** The thresholds array (bindable) */
    thresholds?: NoteThreshold[];
    /** Additional CSS classes */
    class?: string;
    /** Tracker mode for display formatting */
    mode?: "Duration" | "Event";
    /** Lifespan hours for negative threshold validation (Duration mode only) */
    lifespanHours?: number | undefined;
  }

  let {
    thresholds = $bindable([]),
    class: className,
    mode = "Event",
    lifespanHours,
  }: Props = $props();

  function createThreshold(): NoteThreshold {
    return {
      hoursOffset: undefined,
      description: "",
    };
  }
</script>

<ThresholdEditor
  bind:thresholds
  label="Reminders"
  {mode}
  {lifespanHours}
  {createThreshold}
  hoursField="hoursOffset"
  class={className}
  emptyDescription="Add reminders to get notified before the tracker is due"
/>
