<script lang="ts">
  import type { Snippet } from "svelte";

  interface Props {
    href: string | null;
    class?: string;
    children: Snippet;
  }

  const { href, class: className = "", children }: Props = $props();
</script>

{#if href}
  <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- href is an absolute cross-subdomain URL, not an app route -->
  <a {href}
    class="block outline-none transition-colors hover:bg-accent/50 focus-visible:ring-3 focus-visible:ring-inset focus-visible:ring-ring/50 {className}"
    data-testid="person-link"
  >
    {@render children()}
  </a>
{:else}
  <div class={className}>
    {@render children()}
  </div>
{/if}
