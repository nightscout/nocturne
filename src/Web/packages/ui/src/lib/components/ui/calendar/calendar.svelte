<script lang="ts">
  import { Calendar as CalendarPrimitive } from "bits-ui";
  import Cell from "./calendar-cell.svelte";
  import Day from "./calendar-day.svelte";
  import Grid from "./calendar-grid.svelte";
  import Header from "./calendar-header.svelte";
  import Months from "./calendar-months.svelte";
  import GridRow from "./calendar-grid-row.svelte";
  import Heading from "./calendar-heading.svelte";
  import GridBody from "./calendar-grid-body.svelte";
  import GridHead from "./calendar-grid-head.svelte";
  import HeadCell from "./calendar-head-cell.svelte";
  import NextButton from "./calendar-next-button.svelte";
  import PrevButton from "./calendar-prev-button.svelte";
  import { cn, type WithoutChildrenOrChild } from "../../../utils";

  let {
    ref = $bindable(null),
    value = $bindable(),
    placeholder = $bindable(),
    class: className,
    weekdayFormat = "short",
    ...restProps
  }: WithoutChildrenOrChild<CalendarPrimitive.RootProps> = $props();
</script>

<!-- eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- bits-ui types value as a union keyed by `type`, which destructuring for $bindable splits apart -->
<CalendarPrimitive.Root bind:value={value as never}
  bind:ref
  bind:placeholder
  {weekdayFormat}
  class={cn("p-3", className)}
  {...restProps}
>
  {#snippet children({ months, weekdays })}
    <Header>
      <PrevButton />
      <Heading />
      <NextButton />
    </Header>
    <Months>
      {#each months as month (month)}
        <Grid>
          <GridHead>
            <GridRow class="flex">
              {#each weekdays as weekday (weekday)}
                <HeadCell>
                  {weekday.slice(0, 2)}
                </HeadCell>
              {/each}
            </GridRow>
          </GridHead>
          <GridBody>
            {#each month.weeks as weekDates (weekDates)}
              <GridRow class="mt-2 w-full">
                {#each weekDates as date (date)}
                  <Cell {date} month={month.value}>
                    <Day />
                  </Cell>
                {/each}
              </GridRow>
            {/each}
          </GridBody>
        </Grid>
      {/each}
    </Months>
  {/snippet}
</CalendarPrimitive.Root>
