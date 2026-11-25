<script lang="ts">
  import StatusPill from "./StatusPill.svelte";

  interface LinkBatteryPillProps {
    /** Battery voltage of the radio link between the uploader and the pump, in volts. */
    voltage: number;
  }

  let { voltage }: LinkBatteryPillProps = $props();

  const display = $derived(`${voltage.toFixed(2)} V`);
</script>

<StatusPill value={display} label="Link" level="none" />
