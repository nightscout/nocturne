<script lang="ts">
  import { Eye } from "lucide-svelte";

  interface Props {
    expiresAt: string;
  }

  const { expiresAt }: Props = $props();

  let now = $state(Date.now());

  $effect(() => {
    const interval = setInterval(() => {
      now = Date.now();
    }, 60_000);
    return () => clearInterval(interval);
  });

  const remaining = $derived.by(() => {
    const ms = new Date(expiresAt).getTime() - now;
    if (ms <= 0) return "Expired";
    const totalMinutes = Math.floor(ms / 60_000);
    const hours = Math.floor(totalMinutes / 60);
    const minutes = totalMinutes % 60;
    if (hours > 0) return `${hours}h ${minutes}m remaining`;
    return `${minutes}m remaining`;
  });
</script>

<div
  class="sticky top-0 z-50 flex items-center justify-between gap-4 border-b border-amber-200 bg-amber-50 px-4 py-2 text-sm text-amber-900 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200"
>
  <div class="flex items-center gap-2">
    <Eye class="h-4 w-4 shrink-0" />
    <span>Guest access &mdash; read only</span>
  </div>
  <span class="shrink-0 font-medium">{remaining}</span>
</div>
