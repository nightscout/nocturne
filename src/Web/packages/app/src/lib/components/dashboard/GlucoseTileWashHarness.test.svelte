<script lang="ts">
  import type { GlucoseTileVariant } from "@nocturne/ui/glucose";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import GlucoseTileWash, { trackUnwashedFill } from "./GlucoseTileWash.svelte";

  interface Props {
    mills: number | undefined;
    variant: GlucoseTileVariant;
    isLoading?: boolean;
    isStale?: boolean;
  }

  let { mills, variant, isLoading = false, isStale = false }: Props = $props();
  trackUnwashedFill(() => ({ loading: isLoading, stale: isStale, disconnected: false, variant }));
</script>

{#snippet wash()}
  <GlucoseTileWash {mills} {variant} delta={0} />
{/snippet}

<GlucoseValueIndicator displayValue="123" {variant} {isLoading} {isStale} size="xl" background={wash} />
