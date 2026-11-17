<script lang="ts">
  import {
    Artwork,
    defaultPaletteFor,
    type ArtworkId,
    type IconArtworkSource,
    type PaletteId,
  } from "water-ui";

  interface Props {
    art: ArtworkId | IconArtworkSource;
    palette?: PaletteId;
    class?: string;
  }

  let { art, palette, class: className }: Props = $props();

  const icon = $derived(typeof art === "string" ? undefined : art);
  const artwork = $derived(typeof art === "string" ? art : undefined);
  const resolvedPalette = $derived(
    palette ?? defaultPaletteFor(typeof art === "string" ? art : `lucide-${art.name}`)
  );
</script>

<Artwork {artwork} {icon} palette={resolvedPalette} motion="auto" autoplay="once" class={className} />
