<!--
  A chart of signals' advanced settings: closed at first. It offers to change the chart signal by
  signal, which keeps everything the chart shows and fixes its view and groups as they are.
-->
<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import SlidersHorizontal from "@lucide/svelte/icons/sliders-horizontal";
  import { describe } from "./describe.js";
  import type { ChartBlock, Report } from "./report.svelte.js";

  interface Props {
    report: Report;
    block: ChartBlock;
  }

  let { report, block }: Props = $props();

  let confirming = $state(false);
  const reason = $derived(block.ws.empty ? "Add a signal first." : null);
</script>

<Collapsible.Root>
  <Collapsible.Trigger>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button {...props} variant="subtle" size="xs" class="group" data-testid="block-advanced">
        <ChevronRight
          class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
          aria-hidden="true"
        />
        Advanced
      </Button>
    {/snippet}
  </Collapsible.Trigger>
  <Collapsible.Content class="space-y-2 pt-2">
    <p class="text-xs text-muted-foreground">
      To draw each signal in a different way, give it its own scale, or add a calculated line from any
      two signals, change the chart signal by signal.
    </p>
    <Button
      variant="outline"
      size="sm"
      disabled={reason !== null}
      {...describe(reason)}
      onclick={() => (confirming = true)}
      data-testid="signal-by-signal"
    >
      <SlidersHorizontal aria-hidden="true" />
      Change each signal on its own
    </Button>
    {#if reason}
      <p class="text-xs text-muted-foreground">{reason}</p>
    {/if}
  </Collapsible.Content>
</Collapsible.Root>

<ConfirmDialog
  open={confirming}
  onOpenChange={(open) => (confirming = open)}
  title="Change this chart signal by signal?"
  confirmLabel="Change signal by signal"
  onConfirm={() => {
    confirming = false;
    report.changeSignalBySignal(block);
  }}
>
  {#snippet description()}
    The chart keeps everything that it shows. After this, you cannot change its view or its groups.
  {/snippet}
</ConfirmDialog>
