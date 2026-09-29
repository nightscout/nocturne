<script lang="ts">
  import { Artwork, type ArtworkPlayer } from "@nocturne/watercolour";
  import { databaseArtwork } from "$lib/watercolour-icons";

  let stored = $state(false);

  function awaitFinish(player: ArtworkPlayer) {
    if (player.state.finished) stored = true;
    return player.on("finished", () => (stored = true));
  }
</script>

<div
  role="status"
  data-testid="migration-complete"
  class="flex items-center gap-4 rounded-lg border border-success/30 bg-success/5 p-4"
>
  {#if stored}
    <Artwork artwork="confirmation-mark" motion="auto" autoplay="once" class="size-16 shrink-0" />
  {:else}
    <Artwork icon={databaseArtwork} motion="auto" autoplay="once" class="size-16 shrink-0" onready={awaitFinish} />
  {/if}
  <div>
    <p class="font-medium">Migration complete</p>
    <p class="text-sm text-muted-foreground">Your data is now in Nocturne. The History tab lists what came across.</p>
  </div>
</div>
