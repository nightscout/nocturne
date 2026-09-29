<script lang="ts">
  import Printer from "@lucide/svelte/icons/printer";
  import { Button } from "$lib/components/ui/button";
  import { PaintedMoment } from "$lib/forms";
  import { printReport } from "./report-print.svelte";

  /** Print dialogs closed so far. print() cannot tell a printed page from a cancelled one. */
  let printed = $state(0);

  async function print() {
    await printReport();
    printed++;
  }
</script>

<PaintedMoment
  count={printed}
  artwork="report-pages"
  artClass="size-7"
  class="text-xs text-muted-foreground print:hidden"
>
  Printed
</PaintedMoment>
<Button variant="outline" size="sm" onclick={print} aria-label="Print report">
  <Printer class="w-4 h-4" />
  <span class="hidden sm:inline">Print</span>
</Button>
