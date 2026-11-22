<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import AccessEndField from "#lib/components/members/AccessEndField.svelte";
  import PermissionList from "#lib/components/teams/PermissionList.svelte";
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

  function toggleRole(roleId: string, checked: boolean) {
    pickedRoleIds = checked ? [...roleIds, roleId] : roleIds.filter((id) => id !== roleId);
  }

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
    <fieldset class="space-y-2">
      <legend class="text-sm font-medium">Roles to give the team</legend>
      {#each site.roles ?? [] as role (role.id)}
        <div class="space-y-1 rounded-md border p-3">
          <div class="flex items-center gap-2">
            <Checkbox
              id={`team-access-role-${role.id}`}
              checked={roleIds.includes(role.id ?? "")}
              onCheckedChange={(checked: boolean) => toggleRole(role.id ?? "", checked === true)}
            />
            <Label for={`team-access-role-${role.id}`} variant="option">{role.name}</Label>
          </div>
          <p class="text-xs text-muted-foreground">What the team can see and change with this role</p>
          <PermissionList permissions={role.permissions ?? []} />
        </div>
      {/each}
      {#if roleIds.length === 0}
        <p class="text-xs text-muted-foreground">Choose at least one role.</p>
      {/if}
    </fieldset>

    <div class="flex items-start gap-2 rounded-md border bg-muted/30 p-3">
      <Checkbox
        id="team-access-approve-limit"
        checked={limited}
        disabled={site.limitForced === true}
        aria-describedby={site.limitForced ? "team-access-approve-limit-reason" : undefined}
        onCheckedChange={(checked: boolean) => (pickedLimit = checked === true)}
      />
      <div class="flex-1">
        <label for="team-access-approve-limit" class="text-sm font-medium select-none">
          Only the last 24 hours
        </label>
        {#if site.limitForced}
          <p id="team-access-approve-limit-reason" class="mt-0.5 text-xs text-muted-foreground">
            The link or your own access requires this limit.
          </p>
        {:else}
          <p class="mt-0.5 text-xs text-muted-foreground">
            The team can see only the most recent 24 hours of data.
          </p>
        {/if}
      </div>
    </div>

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
