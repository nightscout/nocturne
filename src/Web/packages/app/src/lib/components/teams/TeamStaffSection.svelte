<script lang="ts">
  import { tick } from "svelte";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Pencil from "@lucide/svelte/icons/pencil";
  import UserMinus from "@lucide/svelte/icons/user-minus";
  import StaffNarrowing from "#lib/components/teams/StaffNarrowing.svelte";
  import StaffNarrowingFields, {
    NO_CEILING,
    ceilingSlug,
  } from "#lib/components/teams/StaffNarrowingFields.svelte";
  import {
    accessEndDay,
    accessEndInstant,
    isAccessEndChoiceValid,
  } from "#lib/components/members/access-end.js";
  import { removeStaff, updateStaff } from "#lib/api/generated/teams.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import { plural } from "#lib/utils/plural.js";
  import { teamProblemMessages } from "#lib/teams/problem-messages.svelte.js";
  import type { TeamStaffMember, TeamSuggestableRole } from "@nocturne/api-client";

  interface Props {
    teamId: string;
    teamName: string;
    staff: TeamStaffMember[];
    roles: TeamSuggestableRole[];
  }

  let { teamId, teamName, staff, roles }: Props = $props();

  const now = new Now(60_000);

  let editingId = $state<string | null>(null);
  let ceiling = $state(NO_CEILING);
  let limitTo24Hours = $state(false);
  let endDay = $state("");
  let storedEnd = $state<string | undefined>(undefined);
  let saving = $state(false);
  let editError = $state<string | null>(null);

  const keepsStoredEnd = $derived(!!storedEnd && endDay === accessEndDay(storedEnd));
  const endIsOffered = $derived(
    keepsStoredEnd || isAccessEndChoiceValid(endDay, { now: now.current }),
  );

  function startEdit(member: TeamStaffMember) {
    editingId = member.subjectId ?? null;
    ceiling = member.narrowing?.roleCeilingSlug ?? NO_CEILING;
    limitTo24Hours = member.narrowing?.limitTo24Hours ?? false;
    storedEnd = member.narrowing?.endsAt ?? undefined;
    endDay = storedEnd ? accessEndDay(storedEnd) : "";
    editError = null;
  }

  let list = $state<HTMLUListElement>();

  async function closeEdit() {
    const closedId = editingId;
    editingId = null;
    editError = null;
    if (!closedId) return;
    await tick();
    list?.querySelector<HTMLElement>(`[data-staff-edit="${CSS.escape(closedId)}"]`)?.focus();
  }

  async function save(subjectId: string) {
    if (saving || !endIsOffered) return;
    saving = true;
    editError = null;
    let saved = false;
    try {
      await updateStaff({
        teamId,
        subjectId,
        request: {
          roleCeilingSlug: ceilingSlug(ceiling),
          limitTo24Hours,
          endsAt: keepsStoredEnd ? storedEnd : endDay === "" ? undefined : accessEndInstant(endDay),
        },
      });
      saved = true;
    } catch (err) {
      editError = describeSubmitError(err, "Could not save the change. Try again.", teamProblemMessages());
    } finally {
      saving = false;
    }
    if (saved) await closeEdit();
  }

  let removingId = $state<string | null>(null);
  let removeError = $state<string | null>(null);

  async function remove(subjectId: string) {
    removingId = subjectId;
    removeError = null;
    try {
      await removeStaff({ teamId, subjectId });
      if (editingId === subjectId) editingId = null;
    } catch (err) {
      removeError = describeSubmitError(
        err,
        "Could not remove the staff member. Try again.",
        teamProblemMessages(),
      );
    } finally {
      removingId = null;
    }
  }
</script>

<Card.Root>
  <Card.Header>
    <Card.Title>Staff</Card.Title>
    <Card.Description>
      The people who work for the team. A staff member can see a site when the site lets them in.
    </Card.Description>
  </Card.Header>
  <Card.Content>
    {#if removeError}
      <p class="mb-3 text-sm text-destructive" role="alert">{removeError}</p>
    {/if}
    {#if staff.length === 0}
      <p class="text-sm text-muted-foreground">
        This team has no staff. Make a link in Invite staff to add a staff member.
      </p>
    {:else}
      <ul class="divide-y" bind:this={list}>
        {#each staff as member (member.subjectId)}
          {@const approved = member.sitesApproved ?? 0}
          {@const pending = member.sitesPending ?? 0}
          <li class="space-y-3 py-3">
            <div class="flex flex-col gap-2 @md:flex-row @md:items-start @md:justify-between">
              <div class="min-w-0 space-y-1">
                <p class="truncate font-medium">{member.displayName}</p>
                <p class="text-sm text-muted-foreground">
                  <StaffNarrowing narrowing={member.narrowing} {roles} hasEnded={member.hasEnded} />
                </p>
                {#if !member.hasEnded}
                  <p class="text-sm text-muted-foreground">
                    {#if approved > 0}
                      {plural(approved, ["Approved at # site.", "Approved at # sites."])}
                    {/if}
                    {#if pending > 0}
                      {plural(pending, ["Waits for approval at # site.", "Waits for approval at # sites."])}
                    {/if}
                    {#if approved === 0 && pending === 0}
                      Not approved at any site.
                    {/if}
                  </p>
                {/if}
              </div>
              <div class="flex shrink-0 gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  aria-label={`Edit ${member.displayName}`}
                  aria-expanded={editingId === member.subjectId}
                  data-staff-edit={member.subjectId}
                  disabled={saving || removingId !== null}
                  onclick={() =>
                    editingId === member.subjectId ? closeEdit() : startEdit(member)}
                >
                  <Pencil class="mr-2 h-4 w-4" />
                  Edit
                </Button>
                <ConfirmDialog
                  title="Remove staff member"
                  confirmLabel="Remove staff member"
                  destructive
                  onConfirm={() => remove(member.subjectId ?? "")}
                >
                  {#snippet trigger(props)}
                    <Button
                      {...props}
                      variant="outline-destructive"
                      size="sm"
                      aria-label={`Remove ${member.displayName}`}
                      disabled={saving || removingId !== null}
                    >
                      {#if removingId === member.subjectId}
                        <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                      {:else}
                        <UserMinus class="mr-2 h-4 w-4" />
                      {/if}
                      Remove
                    </Button>
                  {/snippet}
                  {#snippet description()}
                    Remove {member.displayName} from {teamName}? They lose what the team gives them at every site.
                  {/snippet}
                </ConfirmDialog>
              </div>
            </div>

            {#if editingId === member.subjectId}
              <form
                class="space-y-4 rounded-lg border bg-muted/30 p-4"
                novalidate
                aria-label={`Change the access of ${member.displayName}`}
                onsubmit={(e) => {
                  e.preventDefault();
                  void save(member.subjectId ?? "");
                }}
              >
                <StaffNarrowingFields
                  id={`team-staff-${member.subjectId}`}
                  {roles}
                  bind:ceiling
                  bind:limitTo24Hours
                  bind:endDay
                  now={now.current}
                  disabled={saving}
                />
                <p class="text-sm text-muted-foreground">
                  If a site approves the team's staff itself, it must approve them again when the change gives them more.
                </p>
                {#if editError}
                  <p class="text-sm text-destructive" role="alert">{editError}</p>
                {/if}
                <div class="flex gap-2">
                  <Button type="submit" disabled={saving || !endIsOffered}>
                    {#if saving}
                      <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                    {/if}
                    Save
                  </Button>
                  <Button type="button" variant="ghost" disabled={saving} onclick={closeEdit}>
                    Cancel
                  </Button>
                </div>
              </form>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </Card.Content>
</Card.Root>
