<script lang="ts">
  // Mounts the real preview so the assertion covers its own reactivity. A
  // harness that re-rendered the component itself would pass whether or not the
  // preview keeps deriving its value from the source.
  import type { ClockGlucoseSource } from "$lib/stores/realtime-store.svelte";
  import type { InternalElement } from "$lib/clock-builder";
  import ClockElementPreview from "./ClockElementPreview.svelte";

  interface Props {
    element: InternalElement;
    initialGlucose: ClockGlucoseSource;
    now: Date;
    /** Hands the test the setter for the source the preview is mounted with. */
    onready: (setGlucose: (glucose: ClockGlucoseSource) => void) => void;
  }

  let { element, initialGlucose, now, onready }: Props = $props();

  // svelte-ignore state_referenced_locally
  let glucose = $state(initialGlucose);
  // svelte-ignore state_referenced_locally
  onready((next) => (glucose = next));
</script>

<ClockElementPreview {element} {glucose} {now} trackerDefinitions={[]} />
