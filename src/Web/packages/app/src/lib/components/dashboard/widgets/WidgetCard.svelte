<script lang="ts">
  import type { Snippet } from "svelte";

  interface Props {
    /** Widget title displayed in header */
    title: string;
    /** Optional subtitle/description (text) */
    subtitle?: string;
    /** Optional custom subtitle snippet (takes precedence over subtitle text) */
    subtitleSnippet?: Snippet;
    children: Snippet;
    class?: string;
    /** Tighter vertical padding */
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

<!-- One cell of the dashboard's widget panel; WidgetGrid draws the panel and the rules between cells. -->
<section class="flex h-full flex-col gap-3 px-5 {compact ? 'py-3' : 'py-4'} {className}">
  <header class="flex min-h-7 items-center justify-between gap-2">
    <h2 class="text-sm font-medium text-muted-foreground">{title}</h2>
    {#if subtitleSnippet}
      {@render subtitleSnippet()}
    {:else if subtitle}
      <p class="text-xs text-muted-foreground">{subtitle}</p>
    {/if}
  </header>
  <div class="flex-1">
    {@render children()}
  </div>
</section>
