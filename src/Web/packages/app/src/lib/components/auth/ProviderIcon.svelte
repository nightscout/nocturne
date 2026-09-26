<script lang="ts">
  import { Globe } from "lucide-svelte";

  const { slug, size = 20 }: { slug?: string | null; size?: number } = $props();

  const isUrl = $derived(
    !!slug && (slug.startsWith("http://") || slug.startsWith("https://"))
  );
  const knownSlugs = ["google", "apple", "microsoft", "github"];
  const isKnown = $derived(!!slug && knownSlugs.includes(slug));
</script>

{#if isKnown && slug}
  <div
    class="flex size-(--icon-size) items-center justify-center"
    style:--icon-size="{size}px"
  >
    <Globe {size} />
  </div>
{:else if isUrl && slug}
  <img src={slug} alt="Provider icon" width={size} height={size} class="rounded" />
{:else}
  <Globe {size} />
{/if}
