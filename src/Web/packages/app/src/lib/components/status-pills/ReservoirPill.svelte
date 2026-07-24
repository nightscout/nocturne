<script lang="ts">
  import StatusPill from "./StatusPill.svelte";

  interface ReservoirPillProps {
    /** Insulin remaining in the pump reservoir, in units. */
    reservoir: number;
  }

  let { reservoir }: ReservoirPillProps = $props();

  const display = $derived(`${reservoir.toFixed(1)} U`);
</script>

<StatusPill value={display} label="Reservoir" level="none" />
