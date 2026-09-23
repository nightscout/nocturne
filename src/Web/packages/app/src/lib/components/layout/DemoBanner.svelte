<script lang="ts">
  import { FlaskConical } from "lucide-svelte";
  import { Banner } from "$lib/components/ui/banner";

  interface Props {
    nextResetAt: string | null;
  }

  const { nextResetAt }: Props = $props();

  let now = $state(Date.now());

  $effect(() => {
    const interval = setInterval(() => {
      now = Date.now();
    }, 60_000);
    return () => clearInterval(interval);
  });

  const remaining = $derived.by(() => {
    if (!nextResetAt) return null;
    const ms = new Date(nextResetAt).getTime() - now;
    if (ms <= 0) return "Resetting soon";
    const totalMinutes = Math.floor(ms / 60_000);
    const hours = Math.floor(totalMinutes / 60);
    const minutes = totalMinutes % 60;
    if (hours > 0) return `Resets in ${hours}h ${minutes}m`;
    return `Resets in ${minutes}m`;
  });
</script>

<Banner variant="info">
  <div class="flex items-center gap-2">
    <FlaskConical class="h-4 w-4 shrink-0" />
    <span>Demo instance with synthetic data</span>
  </div>
  {#if remaining}
    <span class="shrink-0 font-medium">{remaining}</span>
  {/if}
</Banner>
