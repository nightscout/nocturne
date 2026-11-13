<!--
  One figure of a two-period comparison: its name, period A's value (muted), period B's, a bar
  that grows from the centre with the change's sign and size, and the change. The change is
  coloured only where the caller says the change is better or worse; with no verdict it stays in
  the text colour, since for many figures neither way is better. Lays out as a grid row inside an
  `@container` at least 42rem wide, and wraps under it.
-->
<script lang="ts">
  import type { ComparisonBar } from "./comparison.js";

  interface Props extends ComparisonBar {
    label: string;
    aText: string;
    bText: string;
    deltaText: string;
    /** Whether B's value is better or worse than A's; null where neither is, or nothing changed. */
    verdict?: "better" | "worse" | null;
  }

  let { label, aText, bText, deltaText, bar, barWidth, verdict = null }: Props = $props();
</script>

<div
  class="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-3 py-2.5 print:py-1.5 @2xl:grid @2xl:flex-nowrap @2xl:gap-4 @2xl:[grid-template-columns:minmax(140px,1fr)_90px_90px_minmax(120px,2fr)_100px]"
  data-testid="comparison-row"
>
  <div class="w-full text-sm font-medium @2xl:w-auto">{label}</div>
  <div class="text-sm tabular-nums text-muted-foreground @2xl:text-right">{aText}</div>
  <div class="text-sm font-semibold tabular-nums @2xl:text-right">{bText}</div>
  <div class="relative order-last h-2 w-full overflow-hidden rounded-full bg-muted @2xl:order-none @2xl:w-auto">
    <div class="absolute top-0 bottom-0 left-1/2 w-px bg-border"></div>
    <div
      class={[
        "absolute top-0 bottom-0 rounded-full transition-all duration-200",
        verdict === "better" ? "bg-status-normal" : verdict === "worse" ? "bg-status-critical" : "bg-foreground",
        bar === "flat" ? "left-[calc(50%-1px)] w-0.5" : bar === "up" ? "left-1/2 w-(--bar-w)" : "right-1/2 w-(--bar-w)",
      ]}
      style:--bar-w="{barWidth}%"
      data-testid="comparison-bar"
      data-bar={bar}
    ></div>
  </div>
  <div
    class={[
      "ml-auto text-sm font-semibold tabular-nums @2xl:ml-0 @2xl:text-right",
      verdict === "better" && "text-status-normal",
      verdict === "worse" && "text-status-critical",
    ]}
    data-testid="comparison-delta"
    data-verdict={verdict ?? ""}
  >
    {deltaText}{#if verdict}<span class="sr-only"> ({verdict})</span>{/if}
  </div>
</div>
