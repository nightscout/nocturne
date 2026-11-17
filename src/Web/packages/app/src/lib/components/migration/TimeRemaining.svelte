<script lang="ts">
  let {
    remaining,
  }: {
    /** A .NET TimeSpan as the API writes it: "[d.]hh:mm:ss[.fffffff]". */
    remaining?: string;
  } = $props();

  const minutes = $derived.by(() => {
    const [head, mins, secs] = (remaining ?? "").split(":");
    if (secs === undefined) return null;
    const [hours, days = "0"] = head.split(".").reverse();
    const total =
      Number(days) * 1440 + Number(hours) * 60 + Number(mins) + parseFloat(secs) / 60;
    return Number.isFinite(total) ? total : null;
  });
</script>

{#if minutes !== null}
  {#if minutes < 1}
    Less than 1 minute remaining
  {:else if minutes < 60}
    About {Math.round(minutes)} min remaining
  {:else}
    About {Math.floor(minutes / 60)} h {Math.round(minutes % 60)} min remaining
  {/if}
{/if}
