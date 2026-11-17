<script lang="ts">
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";

  /** The name the device request gives for itself. Never shown as established. */
  let { claimedName }: { claimedName: string } = $props();
</script>

<div
  role="note"
  class="flex items-start gap-3 rounded-md border border-warning/30 bg-warning/10 p-3"
>
  <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-warning" />
  <p class="text-sm text-warning">
    This request says it is from <span class="font-semibold">{claimedName}</span>,
    but a device sign-in cannot prove which app started it. Only approve if you
    started this sign-in yourself on a device you trust.
  </p>
</div>
