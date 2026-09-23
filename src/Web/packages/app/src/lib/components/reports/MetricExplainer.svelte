<script lang="ts">
  import * as Popover from "$lib/components/ui/popover";
  import { HelpCircle } from "lucide-svelte";

  interface Props {
    term: string;
    shortExplanation: string;
    longExplanation?: string;
    clinicalContext?: string;
    targetRange?: string;
    source?: string;
  }

  let {
    term,
    shortExplanation,
    longExplanation,
    clinicalContext,
    targetRange,
    source,
  }: Props = $props();
</script>

<Popover.Root>
  <Popover.Trigger variant="term">
    <span>{term}</span>
    <HelpCircle class="w-3.5 h-3.5 text-muted-foreground" />
  </Popover.Trigger>
  <Popover.Content class="w-80 p-4">
    <div class="space-y-3">
      <div>
        <h4 class="font-semibold text-sm">{term}</h4>
        <p class="text-sm text-muted-foreground mt-1">{shortExplanation}</p>
      </div>

      {#if longExplanation}
        <p class="text-xs text-muted-foreground border-t pt-2">
          {longExplanation}
        </p>
      {/if}

      {#if targetRange}
        <div
          class="bg-success/10 text-success text-xs p-2 rounded"
        >
          <span class="font-medium">Target:</span>
          {targetRange}
        </div>
      {/if}

      {#if clinicalContext}
        <div
          class="bg-info/10 text-info text-xs p-2 rounded"
        >
          <span class="font-medium">Clinical Note:</span>
          {clinicalContext}
        </div>
      {/if}

      {#if source}
        <p class="text-2xs text-muted-foreground/70 italic">
          Source: {source}
        </p>
      {/if}
    </div>
  </Popover.Content>
</Popover.Root>
