<script lang="ts" module>
  export type CalloutType = 'info' | 'warning' | 'danger' | 'tip';
</script>

<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    type = 'info',
    title,
    children,
  }: {
    type?: CalloutType;
    title?: string;
    children: Snippet;
  } = $props();

  const frame: Record<CalloutType, string> = {
    info: 'border-info/30 bg-info/5',
    warning: 'border-warning/30 bg-warning/5',
    danger: 'border-destructive/40 bg-destructive/5',
    tip: 'border-success/30 bg-success/5',
  };
  const heading: Record<CalloutType, string> = {
    info: 'text-info',
    warning: 'text-warning',
    danger: 'text-destructive',
    tip: 'text-success',
  };
</script>

<!-- The body is mdsvex markdown, so it arrives as paragraphs; the frame owns their spacing. -->
<div class="not-prose my-4 rounded-lg border p-4 {frame[type]}" role="note">
  {#if title}
    <p class="mb-1 font-semibold {heading[type]}">{title}</p>
  {/if}
  <div class="space-y-2 text-sm leading-relaxed text-foreground/90 [&_a]:underline [&_a]:underline-offset-4 [&_code]:rounded [&_code]:bg-muted [&_code]:px-1 [&_code]:py-0.5 [&_code]:text-xs [&_strong]:font-semibold [&_strong]:text-foreground">
    {@render children()}
  </div>
</div>
