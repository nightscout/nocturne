<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AccessEndField from "#lib/components/members/AccessEndField.svelte";
  import TeamAccessTermsFields from "#lib/components/members/TeamAccessTermsFields.svelte";
  import {
    accessEndDate,
    accessEndDay,
    accessEndInstant,
    isAccessEndChoiceValid,
  } from "#lib/components/members/access-end.js";
  import { formatMediumDate } from "#lib/utils/formatting.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import type { EditTeamAccessRequest, TenantRoleDto, TenantTeamAccess } from "@nocturne/api-client";

  /** Every field, as the API requires: it is not a patch, and null removes the end. */
  type WholeEdit = Required<Omit<EditTeamAccessRequest, "accessEndsAt">> & { accessEndsAt: string | null };

  interface Props {
    team: TenantTeamAccess;
    /** The site's roles, each saying whether the caller may give it to a team. */
    roles: TenantRoleDto[];
    /** The caller reads only the last 24 hours, so the access they set must keep that limit. */
    limitForced: boolean;
    /** When the caller's own access ends. The access cannot outlast it. */
    callerAccessEndsAt?: string;
    saving: boolean;
    onSave: (request: WholeEdit) => void;
    onCancel: () => void;
  }

  let { team, roles, limitForced, callerAccessEndsAt, saving, onSave, onCancel }: Props = $props();

  const now = new Now(60_000);
  const idPrefix = $derived(`team-access-edit-${team.teamId}`);

  const offered = $derived(roles.filter((r) => r.canGiveToTeam === true));
  const heldNotOffered = $derived(
    (team.roles ?? []).filter((held) => !offered.some((r) => r.id === held.id)),
  );

  let pickedRoleIds = $state<string[] | null>(null);
  let pickedLimit = $state<boolean | null>(null);
  let pickedEndDay = $state<string | null>(null);

  const roleIds = $derived(
    pickedRoleIds ??
      (team.roles ?? []).map((r) => r.id ?? "").filter((id) => offered.some((r) => r.id === id)),
  );
  const limited = $derived(limitForced || (pickedLimit ?? team.limitTo24Hours === true));
  const endDay = $derived(
    pickedEndDay ?? (team.accessEndsAt && !team.hasEnded ? accessEndDay(team.accessEndsAt) : ""),
  );

  const endOffered = $derived(
    isAccessEndChoiceValid(endDay, { now: now.current, ceiling: callerAccessEndsAt }, true),
  );
  const canSave = $derived(roleIds.length > 0 && endOffered && !saving);

  function save() {
    if (!canSave) return;
    onSave({
      roleIds,
      limitTo24Hours: limited,
      accessEndsAt: endDay === "" ? null : accessEndInstant(endDay),
    });
  }
</script>

<form
  class="space-y-5"
  aria-label={`Change the access of ${team.teamName}`}
  onsubmit={(e) => {
    e.preventDefault();
    save();
  }}
>
  <p class="text-sm text-muted-foreground">
    The change applies to all staff of the team at once. Less access stops at once, and more access
    never makes approved staff wait again.
  </p>

  {#if offered.length === 0}
    <p class="text-sm text-muted-foreground">
      You cannot give a team any role of this site, so you cannot change this access.
    </p>
  {:else}
    {#if heldNotOffered.length > 0}
      <p class="text-sm text-muted-foreground">
        The team has roles that you cannot give: {heldNotOffered.map((r) => r.name).join(", ")}. If you
        save, the team loses them.
      </p>
    {/if}

    <TeamAccessTermsFields
      {idPrefix}
      roles={offered}
      {roleIds}
      onRoleIdsChange={(ids) => (pickedRoleIds = ids)}
      {limited}
      onLimitChange={(checked) => (pickedLimit = checked)}
      {limitForced}
      limitForcedReason="You can see only the last 24 hours, so the access that you give must keep this limit."
    />

    {#if team.hasEnded && team.accessEndsAt}
      <p class="text-sm text-muted-foreground">
        This access ended on {formatMediumDate(accessEndDate(team.accessEndsAt))}. A new end date, or no
        end date, starts the access again for the approved staff.
      </p>
    {/if}

    <AccessEndField
      id={`${idPrefix}-end`}
      bind:value={() => endDay, (day) => (pickedEndDay = day)}
      now={now.current}
      ceiling={callerAccessEndsAt}
      required={!!callerAccessEndsAt}
    />

    <div class="flex flex-wrap gap-2">
      <Button type="submit" disabled={!canSave}>
        {#if saving}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Save
      </Button>
      <Button type="button" variant="outline" disabled={saving} onclick={onCancel}>Cancel</Button>
    </div>
  {/if}
</form>
