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
  <Popover.Trigger
    class="inline-flex items-center gap-1 cursor-help underline decoration-dotted decoration-muted-foreground/50 underline-offset-2"
  >
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
          class="bg-green-50 dark:bg-green-950 text-green-700 dark:text-green-300 text-xs p-2 rounded"
        >
          <span class="font-medium">Target:</span>
          {targetRange}
        </div>
      {/if}

      {#if clinicalContext}
        <div
          class="bg-blue-50 dark:bg-blue-950 text-blue-700 dark:text-blue-300 text-xs p-2 rounded"
        >
          <span class="font-medium">Clinical Note:</span>
          {clinicalContext}
        </div>
      {/if}

      {#if source}
        <p class="text-[10px] text-muted-foreground/70 italic">
          Source: {source}
        </p>
      {/if}
    </div>
  </Popover.Content>
</Popover.Root>
