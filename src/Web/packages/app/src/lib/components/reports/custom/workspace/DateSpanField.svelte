<!--
  Two local dates, picked on the app's range calendar in a popover. The trigger shows the dates, or
  the placeholder until both are picked.
-->
<script lang="ts">
  import { parseDate, type DateValue } from "@internationalized/date";
  import type { DateRange } from "bits-ui";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import { RangeCalendar } from "#lib/components/ui/range-calendar/index.js";
  import { formatLocale } from "#lib/utils/formatting.js";
  import CalendarDays from "@lucide/svelte/icons/calendar-days";
  import { spanLabel } from "./clock.js";
  import type { DateSpan } from "./workspace.svelte.js";

  interface Props {
    value: DateSpan | null;
    onchange: (span: DateSpan) => void;
    placeholder: string;
    /** The trigger's accessible name before the dates. */
    label: string;
    open?: boolean;
  }

  let {
    value,
    onchange,
    placeholder,
    label,
    open = $bindable(false),
  }: Props = $props();

  const range = $derived<DateRange | undefined>(
    value
      ? { start: parseDate(value.from), end: parseDate(value.to) }
      : undefined
  );

  const text = spanLabel;

  function pick(next: DateRange | undefined) {
    const start: DateValue | undefined = next?.start;
    const end: DateValue | undefined = next?.end;
    if (!start || !end) return;
    onchange({ from: start.toString(), to: end.toString() });
    open = false;
  }
</script>

<Popover.Root bind:open>
  <Popover.Trigger>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button
        {...props}
        variant="outline"
        size="xs"
        aria-label="{label}: {value ? text(value) : placeholder}"
      >
        <CalendarDays aria-hidden="true" />
        {value ? text(value) : placeholder}
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content align="start" class="w-auto p-0">
    <RangeCalendar
      value={range}
      captionLayout="dropdown"
      locale={formatLocale()}
      onValueChange={pick}
    />
  </Popover.Content>
</Popover.Root>
