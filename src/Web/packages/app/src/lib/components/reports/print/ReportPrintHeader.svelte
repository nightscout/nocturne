<!--
  Identifies a printed report: whose data, which period, when it was printed.
  The first page carries it in full; every page carries a one-line summary and
  page number in the `@page` footer, so a loose sheet can still be placed.
-->
<script lang="ts">
  import { getReportSubject } from "$api/reports.remote";
  import { formatLocale, formatMediumDateRange, formatMediumDateTime } from "$lib/utils/formatting";
  import { dayCount, endOfDay, startOfDay } from "$lib/utils/date-range";
  import { cssString } from "./css-string";
  import type { ReportPrintMeta } from "./report-print.svelte";

  interface Props {
    title: string;
    period: ReportPrintMeta["period"];
  }

  let { title, period }: Props = $props();

  const subject = getReportSubject();
  const name = $derived(subject.current?.name ?? null);
  const dateOfBirth = $derived.by(() => {
    const dob = subject.current?.dateOfBirth;
    if (!dob) return null;
    // A calendar date sent as UTC midnight; formatting it in local time would
    // move it a day for anyone west of Greenwich.
    return new Date(dob).toLocaleDateString(formatLocale(), {
      year: "numeric",
      month: "short",
      day: "numeric",
      timeZone: "UTC",
    });
  });

  const periodText = $derived.by(() => {
    if (!period) return null;
    if ("label" in period) return period.label;
    const days = dayCount(period.from, period.to);
    const range = formatMediumDateRange(startOfDay(period.from), endOfDay(period.to));
    return days === 1 ? range : `${range} (${days} days)`;
  });

  let printedAt = $state(new Date());
  $effect(() => {
    const stamp = () => (printedAt = new Date());
    window.addEventListener("beforeprint", stamp);
    return () => window.removeEventListener("beforeprint", stamp);
  });

  const footer = $derived(
    [name, title, periodText].filter((part): part is string => !!part).join("  ·  ")
  );
  const pageRules = $derived(
    `<style>@media print { @page { ` +
      `@bottom-left { content: ${cssString(footer)}; font: 7.5pt Montserrat, sans-serif; color: #444; } ` +
      `@bottom-right { content: counter(page) " / " counter(pages); font: 7.5pt Montserrat, sans-serif; color: #444; } ` +
      `} }</style>`
  );
</script>

<svelte:head>
  <!-- eslint-disable-next-line svelte/no-at-html-tags -- every interpolated value passes through cssString -->
  {@html pageRules}
</svelte:head>

{#snippet writeIn()}
  <span class="inline-block w-48 border-b border-foreground/60">&nbsp;</span>
{/snippet}

<header class="mb-4 hidden px-3 print:block">
  <div class="flex items-baseline justify-between gap-4 border-b-2 border-foreground pb-1">
    <h1 class="text-lg font-bold text-foreground">{title}</h1>
    <span class="text-xs font-medium text-muted-foreground">Nocturne</span>
  </div>
  <dl class="mt-2 grid grid-cols-[auto_1fr_auto_1fr] items-baseline gap-x-3 gap-y-1 text-xs">
    <dt class="text-muted-foreground">Patient</dt>
    <dd class="font-semibold text-foreground">{#if name}{name}{:else}{@render writeIn()}{/if}</dd>
    <dt class="text-muted-foreground">Date of birth</dt>
    <dd class="text-foreground">{#if dateOfBirth}{dateOfBirth}{:else}{@render writeIn()}{/if}</dd>
    {#if periodText}
      <dt class="text-muted-foreground">Period</dt>
      <dd class="text-foreground">{periodText}</dd>
    {/if}
    <dt class="text-muted-foreground">Printed</dt>
    <dd class="text-foreground">{formatMediumDateTime(printedAt)}</dd>
  </dl>
</header>
