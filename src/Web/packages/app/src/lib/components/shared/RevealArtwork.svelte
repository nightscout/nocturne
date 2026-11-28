<script lang="ts">
  import type { ComponentProps } from "svelte";
  import { Artwork, getEngineHost, getPresentation, WARM_IDLE_TIMEOUT_MS } from "water-ui";
  import { whenIdle } from "#lib/utils/when-idle.js";

  /**
   * An `Artwork` that reveals on load on a page outside the authenticated layout, which is the only
   * host that warms the engine.
   *
   * Under `animated`, a player created while the engine is cold draws its still and keeps it (the
   * library's `stillUntilReady`), so a page's own artwork mounted at hydration never plays. This one
   * holds its slot empty, warms the engine at the next idle period, and mounts once that settles:
   * live where there is a GPU, else the baked or static path the player would have taken anyway.
   * `still` and `off` boot nothing, so under them, or with the engine already up, it mounts at once.
   */
  let artworkProps: ComponentProps<typeof Artwork> = $props();

  // Unset on the server and through hydration, which render the held slot whatever the engine.
  let settled = $state(false);

  $effect(() => {
    if (settled) return;
    const engine = getEngineHost();
    if (getPresentation() !== "animated" || (engine.ready && !engine.booting)) {
      settled = true;
      return;
    }
    let current = true;
    const cancel = whenIdle(
      () => {
        void engine
          .warm()
          .then(() => engine.whenNotBooting())
          .then(() => {
            if (current) settled = true;
          });
      },
      { timeout: WARM_IDLE_TIMEOUT_MS },
    );
    return () => {
      current = false;
      cancel();
    };
  });
</script>

{#if settled}
  <Artwork {...artworkProps} />
{:else}
  <div class={artworkProps.class} aria-hidden="true"></div>
{/if}
