<script lang="ts">
  import Printer from "@lucide/svelte/icons/printer";
  import { Button } from "#lib/components/ui/button/index.js";
  import { PaintedMoment } from "#lib/forms/index.js";

  interface Props {
    /** Runs the print flow; resolves true once the print dialog has closed. */
    print: () => Promise<boolean>;
    disabled?: boolean;
  }

  let { print, disabled = false }: Props = $props();

  /** Print dialogs closed so far. print() cannot tell a printed page from a cancelled one. */
  let printed = $state(0);

  async function onclick() {
    if (await print()) printed++;
  }
</script>

<PaintedMoment
  count={printed}
  artwork="report-pages"
  artClass="size-7"
  class="text-xs text-muted-foreground print:hidden"
>
  Sent to print
</PaintedMoment>
<Button variant="outline" size="sm" {onclick} {disabled} aria-label="Print report">
  <Printer class="w-4 h-4" />
  <span class="hidden @lg:inline">Print</span>
</Button>
