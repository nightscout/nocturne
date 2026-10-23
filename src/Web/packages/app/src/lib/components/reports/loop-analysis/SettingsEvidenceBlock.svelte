<!--
  One block of a setting, as a disclosure: its summary row names the block, the observed value
  against the profile's (and the compared range's), n and the confidence; opened, it says why the
  confidence is what it is and what would raise it, why it did not use some candidates, and lists the
  episodes, each opening Day in Review. A figure towards more insulin says so on its summary row,
  and carries the observations-not-advice note in its body.
-->
<script lang="ts" generics="E extends SettingsEvidenceEpisode">
  import { resolve } from "$app/paths";
  import {
    SettingsEvidenceConfidence,
    type SettingsEvidenceEpisode,
    type SettingsEvidenceFigure,
    type SettingsEvidenceRules,
  } from "$lib/api";
  import { Badge } from "$lib/components/ui/badge";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import { formatWeekdayDate, hourLabel, time } from "$lib/utils/formatting";
  import { startOfDay } from "$lib/utils/date-range";
  import { plural } from "$lib/utils/plural";
  import ArrowUpRight from "@lucide/svelte/icons/arrow-up-right";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Info from "@lucide/svelte/icons/info";
  import ShieldCheck from "@lucide/svelte/icons/shield-check";
  import ShieldHalf from "@lucide/svelte/icons/shield-half";
  import ShieldAlert from "@lucide/svelte/icons/shield-alert";
  import Stethoscope from "@lucide/svelte/icons/stethoscope";
  import { atMinute } from "./daily-profile-view";
  import { clockToMinute } from "./event-response-filter";
  import { episodeSearch, type EpisodeColumn } from "./settings-evidence-view";
  import { confidenceLabel, reasonText, ruleText, unavailableText, type SettingsEvidenceSection } from "./settings-evidence-labels.svelte";

  interface Props {
    figure: SettingsEvidenceFigure;
    compared?: SettingsEvidenceFigure;
    comparing: boolean;
    section: SettingsEvidenceSection;
    format: (value: number) => string;
    unit: string;
    episodes: E[];
    columns: EpisodeColumn<E>[];
    /** Further lines under the figure, such as the loop's mean correction for basal. */
    details?: string[];
    rules: SettingsEvidenceRules | undefined;
  }

  let { figure, compared, comparing, section, format, unit, episodes, columns, details = [], rules }: Props = $props();

  const span = $derived(`${hourLabel(atMinute((figure.startHour ?? 0) * 60))}–${hourLabel(atMinute((figure.endHour ?? 0) * 60))}`);
  const value = (v: number | null | undefined) => (v == null ? "–" : format(v));
  const rejections = $derived((figure.rejections ?? []).filter((r) => (r.count ?? 0) > 0));
  const localTime = (clock: string | undefined) => time(atMinute(clockToMinute(clock ?? "") ?? 0));
  let open = $state(false);
  const low = $derived(figure.confidence === SettingsEvidenceConfidence.Low);
  const badgeVariant = (confidence: SettingsEvidenceConfidence | undefined) =>
    confidence === SettingsEvidenceConfidence.High ? "success" : confidence === SettingsEvidenceConfidence.Moderate ? "info" : "warning";
</script>

{#snippet badge(confidence: SettingsEvidenceConfidence | undefined)}
  <Badge variant={badgeVariant(confidence)}>
    {#if confidence === SettingsEvidenceConfidence.High}
      <ShieldCheck />
    {:else if confidence === SettingsEvidenceConfidence.Moderate}
      <ShieldHalf />
    {:else}
      <ShieldAlert />
    {/if}
    {confidenceLabel(confidence)}
  </Badge>
{/snippet}

<details class="group border-b last:border-0" bind:open>
  <summary
    class="grid cursor-pointer list-none grid-cols-[1.25rem_minmax(5rem,auto)_1fr] items-center gap-x-3 gap-y-1 px-3 py-2 text-sm hover:bg-muted/50 [&::-webkit-details-marker]:hidden @lg:grid-cols-[1.25rem_7rem_1fr_auto]"
  >
    <ChevronRight class="size-4 text-muted-foreground transition-transform group-open:rotate-90" aria-hidden="true" />
    <span class="font-medium tabular-nums">{span}</span>
    <span class="tabular-nums">
      {#if figure.observed != null}
        <span class={low ? "text-muted-foreground" : "font-semibold"} data-testid="observed-value">{format(figure.observed)}</span>
        <span class="text-muted-foreground"> {unit}, profile {value(figure.profile)}</span>
        {#if comparing}
          <span class="text-muted-foreground">
            · compared {value(compared?.observed)}{#if compared?.observed != null}&nbsp;(n = {compared.n ?? 0}, {confidenceLabel(compared.confidence)}){/if}
          </span>
        {/if}
        {#if figure.towardsMoreInsulin}
          <span class="ml-1 inline-flex items-center gap-1 text-xs font-medium whitespace-nowrap text-foreground">
            <Stethoscope class="size-3.5 shrink-0 text-info" aria-hidden="true" />
            More insulin than your profile
          </span>
        {/if}
      {:else}
        <span class="text-muted-foreground">{unavailableText(figure.unavailableReason, section)}</span>
        <span class="text-muted-foreground"> · profile {value(figure.profile)}</span>
      {/if}
    </span>
    <span class="col-start-3 flex flex-wrap items-center gap-2 @lg:col-start-auto">
      <span class="text-xs text-muted-foreground tabular-nums">n = {figure.n ?? 0}</span>
      {#if figure.observed != null}
        {@render badge(figure.confidence)}
      {/if}
    </span>
  </summary>

  {#if open}
  <div class="space-y-3 px-3 pt-1 pb-4 text-sm @lg:pl-11">
    {#if figure.towardsMoreInsulin}
      <p class="flex items-start gap-2 rounded-md border border-info/40 bg-info/10 px-3 py-2">
        <Stethoscope class="mt-0.5 size-4 shrink-0 text-info" aria-hidden="true" />
        <!-- "Points to" on purpose: "shows" would claim more than the evidence gives. -->
        <span>
          Your data points to more insulin here than your profile gives. This is an observation, not
          a setting to copy. Usually, people change settings in small steps, one at a time, with
          their diabetes care team.
        </span>
      </p>
    {:else if figure.observed != null}
      <p>
        {#if figure.towardsLessInsulin}
          Your data points to less insulin here than your profile gives.
        {:else}
          Close to your profile.
        {/if}
      </p>
    {/if}

    {#if figure.observed != null && figure.spread}
      <p class="text-muted-foreground tabular-nums">
        Middle half of the episodes: {value(figure.spread.p25)}–{value(figure.spread.p75)} {unit}
      </p>
    {/if}
    {#each details as line (line)}
      <p class="text-muted-foreground tabular-nums">{line}</p>
    {/each}

    {#if (figure.confidenceReasons ?? []).length > 0}
      <div class="space-y-1">
        <h4 class="font-medium">Why the confidence is not higher</h4>
        <ul class="space-y-1">
          {#each figure.confidenceReasons ?? [] as reason (reason)}
            {@const text = reasonText(reason)}
            <li>{text.why} <span class="text-muted-foreground">{text.raise}</span></li>
          {/each}
        </ul>
      </div>
    {/if}

    {#if rejections.length > 0}
      <div class="space-y-1">
        <h4 class="font-medium">Candidates not used</h4>
        <ul class="grid gap-x-6 gap-y-0.5 @lg:grid-cols-2">
          {#each rejections as rejection (rejection.rule)}
            <li class="flex justify-between gap-3">
              <span class="text-muted-foreground">{ruleText(rejection.rule, rules)}</span>
              <span class="tabular-nums">{rejection.count}</span>
            </li>
          {/each}
        </ul>
      </div>
    {/if}

    {#if episodes.length > 0}
      <div class="space-y-1">
        <h4 class="font-medium">{plural(episodes.length, ["# episode", "# episodes"])}</h4>
        <Tooltip.Provider>
        <div class="overflow-x-auto rounded-md border">
          <table class="w-full text-xs tabular-nums">
            <caption class="sr-only">Episodes for the {span} figure. Open an episode in Day in Review.</caption>
            <thead>
              <tr class="border-b text-muted-foreground">
                <th scope="col" class="px-2 py-1.5 text-left font-medium">When</th>
                {#each columns as column (column.label)}
                  <th scope="col" class={["px-2 py-1.5 text-right align-bottom font-medium", column.wide && "hidden @lg:table-cell"]}>
                    {#if column.hint}
                      <Tooltip.Root>
                        <Tooltip.Trigger class="inline-flex items-center gap-1">
                          {column.label}
                          <Info class="size-3" aria-hidden="true" />
                        </Tooltip.Trigger>
                        <Tooltip.Content class="max-w-64 text-left">{column.hint}</Tooltip.Content>
                      </Tooltip.Root>
                    {:else}
                      {column.label}
                    {/if}
                  </th>
                {/each}
                <th scope="col" class="px-2 py-1.5"><span class="sr-only">Open in Day in Review</span></th>
              </tr>
            </thead>
            <tbody>
              {#each episodes as episode (episode.at)}
                <tr class="border-b last:border-0 hover:bg-muted/50">
                  <th scope="row" class="px-2 py-1 text-left font-normal whitespace-nowrap">
                    {formatWeekdayDate(startOfDay(episode.date ?? ""))}, {localTime(episode.time)}
                  </th>
                  {#each columns as column (column.label)}
                    <td class={["px-2 py-1 text-right whitespace-nowrap", column.wide && "hidden @lg:table-cell"]}>{column.value(episode)}</td>
                  {/each}
                  <td class="px-2 py-1 text-right">
                    <a
                      href={resolve(`/reports/day-in-review?${episodeSearch(episode)}`)}
                      class="inline-flex items-center text-muted-foreground hover:text-foreground"
                      aria-label="Open {formatWeekdayDate(startOfDay(episode.date ?? ''))} {localTime(episode.time)} in Day in Review"
                    >
                      <ArrowUpRight class="size-4" />
                    </a>
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
        </Tooltip.Provider>
      </div>
    {/if}
  </div>
  {/if}
</details>
