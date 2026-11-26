<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Plus from "@lucide/svelte/icons/plus";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import X from "@lucide/svelte/icons/x";
  import TeamStaffReach from "#lib/components/teams/TeamStaffReach.svelte";
  import {
    addGroupPerson,
    addGroupStaff,
    createGroup,
    deleteGroup,
    removeGroupPerson,
    removeGroupStaff,
    renameGroup,
  } from "#lib/api/generated/teams.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { teamNameMaxLength } from "#lib/teams/limits.js";
  import { teamProblemMessages } from "#lib/teams/problem-messages.svelte.js";
  import { REACH_THEIR_GROUPS } from "#lib/teams/staff-reach.svelte.js";
  import type { TeamGroup, TeamSiteAccess, TeamStaffMember } from "@nocturne/api-client";

  interface Props {
    teamId: string;
    groups: TeamGroup[];
    /** A TeamStaffReach value. */
    staffReach: string;
    accessesOutsideGroups: number;
    sites: TeamSiteAccess[];
    staff: TeamStaffMember[];
  }

  let { teamId, groups, staffReach, accessesOutsideGroups, sites, staff }: Props = $props();

  const narrows = $derived(staffReach === REACH_THEIR_GROUPS);
  const currentSites = $derived(sites.filter((s) => !s.hasEnded));
  const currentStaff = $derived(staff.filter((s) => !s.hasEnded));

  /** The command that runs now, so the section runs one at a time. */
  let busy = $state<string | null>(null);
  let error = $state<string | null>(null);

  async function run(key: string, failure: string, command: () => Promise<unknown>): Promise<boolean> {
    if (busy !== null) return false;
    busy = key;
    error = null;
    try {
      await command();
      return true;
    } catch (err) {
      error = describeSubmitError(err, failure, teamProblemMessages());
      return false;
    } finally {
      busy = null;
    }
  }

  let newName = $state("");

  async function create() {
    const name = newName.trim();
    if (!name) return;
    if (await run("create", "Could not add the group. Try again.", () => createGroup({ teamId, request: { name } })))
      newName = "";
  }

  let renamingId = $state<string | null>(null);
  let renameDraft = $state("");

  function startRename(group: TeamGroup) {
    renamingId = group.id ?? null;
    renameDraft = group.name ?? "";
    error = null;
  }

  async function rename(groupId: string) {
    const name = renameDraft.trim();
    if (!name) return;
    if (
      await run(`rename:${groupId}`, "Could not rename the group. Try again.", () =>
        renameGroup({ teamId, groupId, request: { name } }),
      )
    )
      renamingId = null;
  }

  let chosenPerson = $state<Record<string, string>>({});
  let chosenStaff = $state<Record<string, string>>({});
</script>

<Card.Root>
  <Card.Header>
    <Card.Title>Groups</Card.Title>
    <Card.Description>
      Put the people that the team follows and its staff into groups, for example one group for each clinic.
    </Card.Description>
  </Card.Header>
  <Card.Content class="space-y-6">
    <TeamStaffReach {teamId} {staffReach} {accessesOutsideGroups} />

    <form
      class="flex flex-col gap-2 @md:flex-row @md:items-end"
      aria-label="Add a group"
      onsubmit={(e) => {
        e.preventDefault();
        void create();
      }}
    >
      <div class="flex-1 space-y-2">
        <Label for="team-group-name">Group name</Label>
        <Input
          id="team-group-name"
          bind:value={newName}
          maxlength={teamNameMaxLength}
          autocomplete="off"
        />
      </div>
      <Button type="submit" disabled={busy !== null || newName.trim() === ""}>
        {#if busy === "create"}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {:else}
          <Plus class="mr-2 h-4 w-4" />
        {/if}
        Add group
      </Button>
    </form>

    {#if error}
      <p class="text-sm text-destructive" role="alert">{error}</p>
    {/if}

    {#if groups.length === 0}
      <p class="text-sm text-muted-foreground">This team has no groups.</p>
    {:else}
      <ul class="divide-y" aria-label="Groups">
        {#each groups as group (group.id)}
          {@const groupId = group.id ?? ""}
          {@const peopleToAdd = currentSites.filter((s) => !(group.people ?? []).some((p) => p.tenantId === s.tenantId))}
          {@const staffToAdd = currentStaff.filter((s) => !(group.staff ?? []).some((g) => g.subjectId === s.subjectId))}
          {@const empty = (group.people ?? []).length === 0 && (group.staff ?? []).length === 0}
          <li class="space-y-4 py-4" aria-label={group.name}>
            {#if renamingId === groupId}
              <form
                class="flex flex-col gap-2 @md:flex-row @md:items-end"
                aria-label={`Rename the group ${group.name}`}
                onsubmit={(e) => {
                  e.preventDefault();
                  void rename(groupId);
                }}
              >
                <div class="flex-1 space-y-2">
                  <Label for={`team-group-${groupId}-name`}>Group name</Label>
                  <Input
                    id={`team-group-${groupId}-name`}
                    bind:value={renameDraft}
                    maxlength={teamNameMaxLength}
                    autocomplete="off"
                  />
                </div>
                <div class="flex gap-2">
                  <Button
                    type="submit"
                    disabled={busy !== null || renameDraft.trim() === "" || renameDraft.trim() === group.name}
                  >
                    {#if busy === `rename:${groupId}`}
                      <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                    {/if}
                    Save
                  </Button>
                  <Button type="button" variant="ghost" disabled={busy !== null} onclick={() => (renamingId = null)}>
                    Cancel
                  </Button>
                </div>
              </form>
            {:else}
              <div class="flex flex-col gap-2 @md:flex-row @md:items-center @md:justify-between">
                <p class="truncate font-medium">{group.name}</p>
                <div class="flex shrink-0 gap-2">
                  <Button
                    variant="outline"
                    size="sm"
                    aria-label={`Rename the group ${group.name}`}
                    disabled={busy !== null}
                    onclick={() => startRename(group)}
                  >
                    <Pencil class="mr-2 h-4 w-4" />
                    Rename
                  </Button>
                  <ConfirmDialog
                    title="Remove group"
                    confirmLabel="Remove group"
                    destructive
                    onConfirm={() =>
                      run(`delete:${groupId}`, "Could not remove the group. Try again.", () =>
                        deleteGroup({ teamId, groupId }),
                      )}
                  >
                    {#snippet trigger(props)}
                      <Button
                        {...props}
                        variant="outline-destructive"
                        size="sm"
                        aria-label={`Remove the group ${group.name}`}
                        disabled={busy !== null}
                      >
                        {#if busy === `delete:${groupId}`}
                          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                        {:else}
                          <Trash2 class="mr-2 h-4 w-4" />
                        {/if}
                        Remove group
                      </Button>
                    {/snippet}
                    {#snippet description()}
                      {#if narrows}
                        Remove the group {group.name}? Its staff stop seeing its people through the team, unless they are in another group together.
                      {:else}
                        Remove the group {group.name}? No access changes now, because staff see every person the team follows.
                      {/if}
                    {/snippet}
                  </ConfirmDialog>
                </div>
              </div>
            {/if}

            {#if empty}
              <p class="text-sm text-muted-foreground">No one is in this group.</p>
            {:else if group.hasNobodyCurrent}
              <p class="text-sm text-muted-foreground">
                No person or staff member in this group has current access. You can remove the group.
              </p>
            {/if}

            <div class="grid gap-4 @md:grid-cols-2">
              <div class="space-y-2">
                <h3 class="text-sm font-medium">People</h3>
                {#if (group.people ?? []).length === 0 && !empty}
                  <p class="text-sm text-muted-foreground">No person is in this group.</p>
                {:else if !empty}
                  <ul class="space-y-1">
                    {#each group.people ?? [] as person (person.tenantId)}
                      <li class="flex items-center justify-between gap-2 text-sm">
                        <span class="truncate">
                          {person.displayName}{#if person.hasEnded}<span class="text-muted-foreground"> · Access ended</span>{/if}
                        </span>
                        <Button
                          variant="ghost"
                          size="sm"
                          aria-label={`Remove ${person.displayName} from ${group.name}`}
                          disabled={busy !== null}
                          onclick={() =>
                            run(`person:${groupId}`, "Could not remove the person from the group. Try again.", () =>
                              removeGroupPerson({ teamId, groupId, tenantId: person.tenantId ?? "" }),
                            )}
                        >
                          <X class="h-4 w-4" />
                        </Button>
                      </li>
                    {/each}
                  </ul>
                {/if}
                {#if peopleToAdd.length === 0}
                  <p class="text-sm text-muted-foreground">
                    {currentSites.length === 0
                      ? "The team has no current access to a person. A site must give the team access first."
                      : "Every person that the team follows is in this group."}
                  </p>
                {:else}
                  <div class="flex gap-2">
                    <Select.Root
                      type="single"
                      value={chosenPerson[groupId] ?? ""}
                      onValueChange={(v) => (chosenPerson[groupId] = v)}
                      disabled={busy !== null}
                    >
                      <Select.Trigger class="flex-1" aria-label={`Person to add to ${group.name}`} data-testid="group-person-picker">
                        {peopleToAdd.find((s) => s.tenantId === chosenPerson[groupId])?.displayName ?? "Choose a person"}
                      </Select.Trigger>
                      <Select.Content>
                        {#each peopleToAdd as site (site.tenantId)}
                          <Select.Item value={site.tenantId ?? ""} label={site.displayName ?? ""} />
                        {/each}
                      </Select.Content>
                    </Select.Root>
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={busy !== null || !peopleToAdd.some((s) => s.tenantId === chosenPerson[groupId])}
                      onclick={async () => {
                        const tenantId = chosenPerson[groupId] ?? "";
                        if (
                          await run(`person:${groupId}`, "Could not add the person to the group. Try again.", () =>
                            addGroupPerson({ teamId, groupId, tenantId }),
                          )
                        )
                          chosenPerson[groupId] = "";
                      }}
                    >
                      Add person
                    </Button>
                  </div>
                {/if}
              </div>

              <div class="space-y-2">
                <h3 class="text-sm font-medium">Staff</h3>
                {#if (group.staff ?? []).length === 0 && !empty}
                  <p class="text-sm text-muted-foreground">No staff member is in this group.</p>
                {:else if !empty}
                  <ul class="space-y-1">
                    {#each group.staff ?? [] as member (member.subjectId)}
                      <li class="flex items-center justify-between gap-2 text-sm">
                        <span class="truncate">
                          {member.displayName}{#if member.hasEnded}<span class="text-muted-foreground"> · Access ended</span>{/if}
                        </span>
                        <Button
                          variant="ghost"
                          size="sm"
                          aria-label={`Remove ${member.displayName} from ${group.name}`}
                          disabled={busy !== null}
                          onclick={() =>
                            run(`staff:${groupId}`, "Could not remove the staff member from the group. Try again.", () =>
                              removeGroupStaff({ teamId, groupId, subjectId: member.subjectId ?? "" }),
                            )}
                        >
                          <X class="h-4 w-4" />
                        </Button>
                      </li>
                    {/each}
                  </ul>
                {/if}
                {#if staffToAdd.length === 0}
                  <p class="text-sm text-muted-foreground">
                    {currentStaff.length === 0
                      ? "The team has no current staff. Make a link in Invite staff to add a staff member."
                      : "Every current staff member is in this group."}
                  </p>
                {:else}
                  <div class="flex gap-2">
                    <Select.Root
                      type="single"
                      value={chosenStaff[groupId] ?? ""}
                      onValueChange={(v) => (chosenStaff[groupId] = v)}
                      disabled={busy !== null}
                    >
                      <Select.Trigger class="flex-1" aria-label={`Staff member to add to ${group.name}`} data-testid="group-staff-picker">
                        {staffToAdd.find((s) => s.subjectId === chosenStaff[groupId])?.displayName ?? "Choose a staff member"}
                      </Select.Trigger>
                      <Select.Content>
                        {#each staffToAdd as member (member.subjectId)}
                          <Select.Item value={member.subjectId ?? ""} label={member.displayName ?? ""} />
                        {/each}
                      </Select.Content>
                    </Select.Root>
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={busy !== null || !staffToAdd.some((s) => s.subjectId === chosenStaff[groupId])}
                      onclick={async () => {
                        const subjectId = chosenStaff[groupId] ?? "";
                        if (
                          await run(`staff:${groupId}`, "Could not add the staff member to the group. Try again.", () =>
                            addGroupStaff({ teamId, groupId, subjectId }),
                          )
                        )
                          chosenStaff[groupId] = "";
                      }}
                    >
                      Add staff member
                    </Button>
                  </div>
                {/if}
              </div>
            </div>
          </li>
        {/each}
      </ul>
    {/if}
  </Card.Content>
</Card.Root>
