<script lang="ts">
  import type { GlucoseTileVariant } from "@nocturne/ui/glucose";
  import { GlucoseValueIndicator } from "#lib/components/shared/index.js";
  import GlucoseTileWash from "./GlucoseTileWash.svelte";
  import { trackUnwashedFill } from "./glucose-tile-wash-state.svelte";

  interface Props {
    mills: number | undefined;
    variant: GlucoseTileVariant;
    isLoading?: boolean;
    isStale?: boolean;
    isDisconnected?: boolean;
    /** False stands in for a host whose wash module has not arrived yet. */
    washLoaded?: boolean;
  }

  let { mills, variant, isLoading = false, isStale = false, isDisconnected = false, washLoaded = true }: Props =
    $props();
  trackUnwashedFill(() => ({ loading: isLoading, stale: isStale, disconnected: isDisconnected, variant, washed: washLoaded }));
</script>

{#snippet wash()}
  <GlucoseTileWash {mills} {variant} delta={0} />
{/snippet}

<GlucoseValueIndicator
  displayValue="123"
  {variant}
  {isLoading}
  {isStale}
  {isDisconnected}
  size="xl"
  background={washLoaded ? wash : undefined}
/>
