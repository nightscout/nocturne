<script lang="ts">
  import { resolve } from "$app/paths";
  import {
    TenantOverviewAttentionKind,
    type TenantOverviewItem,
  } from "@nocturne/api-client";
  import {
    VALUE_TONE,
    alertLevel,
    alertLevelPrefix,
  } from "#lib/components/status-pills/tracker-pill-style.svelte.js";
  import History from "@lucide/svelte/icons/history";
  import Info from "@lucide/svelte/icons/info";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import { attentionPhrase } from "./attention-phrases.svelte.js";
  import { ruleReasonDetails } from "./flag-rule-text.svelte.js";

  interface Props {
    person: TenantOverviewItem;
    size?: "sm" | "lg" | "xl" | "2xl";
    /** Only the first reason, on one line, cut with an ellipsis. */
    compact?: boolean;
    /** A team rule's team as a link to its page. Off where the reasons are already inside a link. */
    linkTeams?: boolean;
  }

  const { person, size = "sm", compact = false, linkTeams = false }: Props = $props();
  const textClass = $derived(
    { sm: "text-xs", lg: "text-base", xl: "text-xl", "2xl": "text-3xl" }[size]
  );
  const iconClass = $derived(
    { sm: "size-3.5", lg: "size-4", xl: "size-5", "2xl": "size-7" }[size]
  );

  const reasons = $derived(
    (person.attention ?? [])
      .map((reason) => ({
        reason,
        phrase: attentionPhrase(reason, person),
        details: !compact && reason.rule ? ruleReasonDetails(reason.rule) : [],
        team: compact ? undefined : reason.rule?.team,
      }))
      .filter((entry) => entry.phrase !== "")
      .slice(0, compact ? 1 : undefined)
  );
</script>

{#if reasons.length > 0}
  <ul
    class="flex gap-x-3 gap-y-0.5 {compact ? 'min-w-0' : 'flex-wrap'}"
    data-testid="attention-reasons"
  >
    {#each reasons as { reason, phrase, details, team }, index (index)}
      {@const level = alertLevel(reason.level ?? null)}
      {@const prefix = alertLevelPrefix(level)}
      {@const noReading =
        reason.kind === TenantOverviewAttentionKind.NoRecentReading}
      <li
        class="flex min-w-0 items-center gap-1 {textClass} font-medium {noReading
          ? 'text-muted-foreground'
          : VALUE_TONE[level]}"
      >
        {#if noReading}
          <History class="shrink-0 {iconClass}" aria-hidden="true" />
        {:else if prefix}
          <TriangleAlert class="shrink-0 {iconClass}" aria-hidden="true" />
        {:else}
          <Info class="shrink-0 {iconClass}" aria-hidden="true" />
        {/if}
        {#if prefix}
          <span class="sr-only">{prefix}</span>
        {/if}
        <span class={compact ? "truncate" : ""}>{phrase}</span>
        {#each details as detail (detail)}
          <span class="font-normal text-muted-foreground">{detail}</span>
        {/each}
        {#if team?.id && linkTeams}
          <a
            class="font-normal text-muted-foreground underline underline-offset-4"
            href={resolve("/(authenticated)/settings/teams/[teamId]", { teamId: team.id })}
            data-testid="attention-rule-team"
          >
            Team: {team.name}
          </a>
        {:else if team?.id}
          <span class="font-normal text-muted-foreground" data-testid="attention-rule-team">
            Team: {team.name}
          </span>
        {/if}
      </li>
    {/each}
  </ul>
{/if}
