<script lang="ts">
  import { untrack } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import { Clock as ClockIcon, Loader2 } from "lucide-svelte";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import { getById as getClockFaceById } from "$api/generated/clockFaces.generated.remote";
  import ClockFaceRenderer from "$lib/components/clock/ClockFaceRenderer.svelte";

  interface Props {
    /** Absent on a face the list returned without one, which has nothing to fetch. */
    faceId?: string;
  }

  const { faceId }: Props = $props();

  // One query per card, owned by the component that renders it; see RuleBuilderLeafEditor.
  const preview = untrack(() => (faceId ? getClockFaceById(faceId) : null));
</script>

{#if preview?.current?.config}
  <ClockFaceRenderer
    config={preview.current.config}
    scale={0.4}
    showCharts={false}
    class="h-full w-full"
  />
{:else if preview?.loading}
  <div class="flex h-full items-center justify-center bg-neutral-950">
    <Loader2 class="size-6 animate-spin text-muted-foreground" />
  </div>
{:else if preview?.error}
  <div class="flex h-full flex-col items-center justify-center gap-2 bg-muted px-3 text-center">
    <p class="text-xs text-muted-foreground">
      {remoteErrorMessage(preview.error, "Failed to load preview")}
    </p>
    <Button variant="outline" size="sm" onclick={() => void preview.refresh().catch(() => {})}>
      Retry
    </Button>
  </div>
{:else}
  <div class="flex h-full items-center justify-center bg-neutral-950">
    <ClockIcon class="size-6 text-muted-foreground" />
  </div>
{/if}
