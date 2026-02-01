<script lang="ts">
  import { Card, CardContent, CardHeader, CardTitle } from "$lib/components/ui/card";
  import type { Snippet } from "svelte";

  interface Props {
    /** Widget title displayed in header */
    title: string;
    /** Optional subtitle/description (text) */
    subtitle?: string;
    /** Optional custom subtitle snippet (takes precedence over subtitle text) */
    subtitleSnippet?: Snippet;
    /** Primary value to display (large text) */
    children: Snippet;
    /** Additional CSS classes for the card */
    class?: string;
    /** Whether to show compact mode (no header padding) */
    compact?: boolean;
  }

  let {
    title,
    subtitle,
    subtitleSnippet,
    children,
    class: className = "",
    compact = false,
  }: Props = $props();
</script>

<Card class="h-full {className}">
  <CardHeader class={compact ? "pb-1 pt-3" : "pb-2"}>
    <CardTitle class="text-sm font-medium">{title}</CardTitle>
    {#if subtitleSnippet}
      {@render subtitleSnippet()}
    {:else if subtitle}
      <p class="text-xs text-muted-foreground">{subtitle}</p>
    {/if}
  </CardHeader>
  <CardContent class={compact ? "pt-0" : ""}>
    {@render children()}
  </CardContent>
</Card>
