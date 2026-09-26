<script lang="ts" module>
  import type { ArtworkId, IconArtworkSource, PaletteId } from "@nocturne/watercolour";
  import { databaseArtwork, fingerprintArtwork } from "$lib/watercolour-icons";

  type StepArtSource =
    | { artwork: ArtworkId; icon?: undefined; palette: PaletteId }
    | { icon: IconArtworkSource; artwork?: undefined; palette: PaletteId };

  export const STEP_ART = {
    welcome: { artwork: "crescent-moon", palette: "moonlight" },
    account: { icon: fingerprintArtwork, palette: "water" },
    source: { artwork: "plug", palette: "slate" },
    import: { icon: databaseArtwork, palette: "slate" },
    done: { artwork: "confirmation-mark", palette: "moss" },
  } as const satisfies Record<string, StepArtSource>;

  export type StepArt = keyof typeof STEP_ART;
</script>

<script lang="ts">
  import {
    Artwork,
    hostSurface,
    watchSurface,
    type ArtworkPlayer,
  } from "@nocturne/watercolour";

  let {
    art,
    progress,
    class: className,
  }: {
    art: StepArt;
    /** 0..1. When set, the reveal follows it instead of playing on its own. */
    progress?: number;
    class?: string;
  } = $props();

  const source: StepArtSource = $derived(STEP_ART[art]);

  // The player resolves its pigment compositing once, at mount, so a theme
  // change has to remount it.
  let surface = $state(hostSurface());
  $effect(() => watchSurface((next) => (surface = next)));

  let player = $state<ArtworkPlayer | null>(null);
  function handleReady(ready: ArtworkPlayer) {
    player = ready;
    return () => {
      if (player === ready) player = null;
    };
  }

  const autoplay = $derived(progress === undefined ? "once" : "never");

  // Whole percents: each seek replays the simulation from a checkpoint.
  const target = $derived(
    progress === undefined ? undefined : Math.round(progress * 100) / 100
  );

  $effect(() => {
    if (!player || target === undefined) return;
    if (player.state.motion === "reduced") return;
    player.seek(target);
  });
</script>

{#key `${art}:${surface}`}
  <Artwork
    artwork={source.artwork}
    icon={source.icon}
    palette={source.palette}
    {surface}
    motion="auto"
    {autoplay}
    onready={handleReady}
    class={className}
  />
{/key}
