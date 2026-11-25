<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import AccessEndField from "#lib/components/members/AccessEndField.svelte";
  import TeamAccessTermsFields from "#lib/components/members/TeamAccessTermsFields.svelte";
  import {
    latestAccessEndDay,
    accessEndInstant,
    hasOfferedAccessEndDay,
    isAccessEndChoiceValid,
  } from "#lib/components/members/access-end.js";
  import { startOfDay } from "#lib/utils/date-range.js";
  import { formatMediumDate } from "#lib/utils/formatting.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import type { ApproveTeamAccessRequest, TeamAccessSiteOption } from "@nocturne/api-client";

  interface Props {
    site: TeamAccessSiteOption;
    onApprove: (request: ApproveTeamAccessRequest) => void;
  }

  let { site, onApprove }: Props = $props();

  const now = new Now(60_000);

  let pickedRoleIds = $state<string[] | null>(null);
  let pickedLimit = $state(false);
  let endDay = $state("");

  const roleIds = $derived(pickedRoleIds ?? (site.defaultRoleId ? [site.defaultRoleId] : []));
  const limited = $derived(site.limitForced === true || pickedLimit);
  const bounds = $derived({ now: now.current, ceiling: site.latestEnd });
  const endMustBeChosen = $derived(!!site.latestEnd);
  const endIsOffered = $derived(isAccessEndChoiceValid(endDay, bounds, endMustBeChosen));
  const noDayOffered = $derived(!hasOfferedAccessEndDay(bounds));
  const endNote = $derived(
    !site.latestEnd
      ? undefined
      : noDayOffered
        ? "No end date is left that this link and your own access allow."
        : `The access must end on ${formatMediumDate(startOfDay(latestAccessEndDay(site.latestEnd) ?? ""))} or before. The link or your own access sets this limit.`,
  );
  const canApprove = $derived(roleIds.length > 0 && endIsOffered);

  function approve() {
    if (!canApprove) return;
    onApprove({
      tenantId: site.tenantId,
      roleIds,
      limitTo24Hours: limited,
      accessEndsAt: endDay === "" ? undefined : accessEndInstant(endDay),
    });
  }
</script>

<form
  class="space-y-5"
  onsubmit={(e) => {
    e.preventDefault();
    approve();
  }}
>
  {#if (site.roles ?? []).length === 0}
    <p class="text-sm text-muted-foreground">
      No role of this site fits this link and your own access, so the team cannot get access here.
    </p>
  {:else}
    <TeamAccessTermsFields
      idPrefix="team-access-approve"
      roles={site.roles ?? []}
      {roleIds}
      onRoleIdsChange={(ids) => (pickedRoleIds = ids)}
      {limited}
      onLimitChange={(checked) => (pickedLimit = checked)}
      limitForced={site.limitForced === true}
      limitForcedReason="The link or your own access requires this limit."
    />

    <AccessEndField
      id="team-access-approve-end"
      bind:value={endDay}
      now={now.current}
      ceiling={site.latestEnd}
      required={endMustBeChosen}
      limitNote={endNote}
    />

    <Button type="submit" size="lg" class="w-full" disabled={!canApprove}>Give the team access</Button>
  {/if}
</form>
