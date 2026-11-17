<!--
  Chooses how insulin-to-carb ratios are written. Each option shows the same ratio, 10 g per unit,
  in its own format, so the choice reads as an example rather than a code.
-->
<script lang="ts">
  import { Label } from "#lib/components/ui/label/index.js";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "#lib/components/ui/select/index.js";
  import { carbRatioFormat, CARB_RATIO_FORMATS } from "#lib/stores/appearance-store.svelte.js";
  import { formatInsulinToCarbRatio } from "#lib/utils/formatting.js";
</script>

<div class="space-y-2">
  <Label for="carb-ratio-format">Insulin-to-carb ratio (ICR)</Label>
  <Select
    type="single"
    value={carbRatioFormat.current}
    onValueChange={(value) => {
      const format = CARB_RATIO_FORMATS.find((f) => f === value);
      if (format) carbRatioFormat.current = format;
    }}
  >
    <SelectTrigger id="carb-ratio-format">
      <span>{formatInsulinToCarbRatio(10, carbRatioFormat.current)}</span>
    </SelectTrigger>
    <SelectContent>
      {#each CARB_RATIO_FORMATS as format (format)}
        <SelectItem value={format}>{formatInsulinToCarbRatio(10, format)}</SelectItem>
      {/each}
    </SelectContent>
  </Select>
  <p class="text-xs text-muted-foreground">
    How carb ratios are written in reports and bolus details. Each option shows the same ratio:
    10 g of carbs per unit of insulin.
  </p>
</div>
