<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import UsersRound from "@lucide/svelte/icons/users-round";
  import ChevronLeft from "@lucide/svelte/icons/chevron-left";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import LogOut from "@lucide/svelte/icons/log-out";
  import UserMinus from "@lucide/svelte/icons/user-minus";
  import CopyButton from "#lib/components/members/CopyButton.svelte";
  import {
    createManagerInvite,
    deleteTeam,
    getTeam,
    removeManager,
    removeStaff,
    renameTeam,
    revokeManagerInvite,
  } from "#lib/api/generated/teams.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { formatMediumDate, formatMediumDateTime } from "#lib/utils/formatting.js";
  import { accessEndDate } from "#lib/components/members/access-end.js";
  import { inviteLabelMaxLength, teamNameMaxLength } from "#lib/teams/limits.js";
  import { MANAGER_KIND, STAFF_KIND } from "#lib/teams/member-kind.svelte.js";
  import { linkExpiryOptions } from "#lib/teams/link-expiry.svelte.js";
  import { teamProblemMessages } from "#lib/teams/problem-messages.svelte.js";
  import TeamsUnavailable from "#lib/components/teams/TeamsUnavailable.svelte";
  import AccessLinksSection from "#lib/components/teams/AccessLinksSection.svelte";
  import TeamSitesSection from "#lib/components/teams/TeamSitesSection.svelte";
  import TeamStaffSection from "#lib/components/teams/TeamStaffSection.svelte";
  import StaffInvitesSection from "#lib/components/teams/StaffInvitesSection.svelte";
  import StaffNarrowing from "#lib/components/teams/StaffNarrowing.svelte";

  const refused = $derived(page.data.refusedAsDemoSubject === true);
  const teamId = $derived(page.params.teamId ?? "");
  const teamQuery = $derived(refused ? null : getTeam(teamId));
  retainQuery(() => teamQuery);
  const team = $derived(teamQuery?.current);
  const isManager = $derived(team?.callerKind === MANAGER_KIND);
  const isStaff = $derived(team?.callerKind === STAFF_KIND);
  const callerId = $derived(page.data.user?.subjectId);
  const ownEnd = $derived(team?.ownStaff?.hasEnded ? team.ownStaff.narrowing?.endsAt : undefined);

  let renameDraft = $state<string | null>(null);
  const name = $derived(renameDraft ?? team?.name ?? "");
  let renaming = $state(false);
  let renameError = $state<string | null>(null);

  async function rename() {
    const trimmed = name.trim();
    if (!trimmed || renaming) return;
    renaming = true;
    renameError = null;
    try {
      await renameTeam({ teamId, request: { name: trimmed } });
      renameDraft = null;
    } catch (err) {
      renameError = describeSubmitError(
        err,
        "Could not rename the team. Try again.",
        teamProblemMessages(),
      );
    } finally {
      renaming = false;
    }
  }

  let managerError = $state<string | null>(null);
  let removingId = $state<string | null>(null);
  let leaving = $state(false);

  async function removeOrLeave(subjectId: string, isLeaving: boolean) {
    removingId = subjectId;
    leaving = isLeaving;
    managerError = null;
    try {
      await removeManager({ teamId, subjectId });
      if (isLeaving) await goto(resolve("/(authenticated)/settings/teams"));
    } catch (err) {
      leaving = false;
      managerError = describeSubmitError(
        err,
        isLeaving ? "Could not leave the team. Try again." : "Could not remove the manager. Try again.",
        teamProblemMessages(),
      );
    } finally {
      removingId = null;
    }
  }

  let staffLeaveError = $state<string | null>(null);

  async function leaveAsStaff() {
    if (!callerId) return;
    leaving = true;
    staffLeaveError = null;
    try {
      await removeStaff({ teamId, subjectId: callerId });
      await goto(resolve("/(authenticated)/settings/teams"));
    } catch (err) {
      leaving = false;
      staffLeaveError = describeSubmitError(
        err,
        "Could not leave the team. Try again.",
        teamProblemMessages(),
      );
    }
  }

  let inviteLabel = $state("");
  let expiresInDays = $state("7");
  let inviting = $state(false);
  let inviteError = $state<string | null>(null);
  let createdUrl = $state<string | null>(null);
  let createdInviteId = $state<string | null>(null);

  async function createInvite() {
    if (inviting) return;
    inviting = true;
    inviteError = null;
    createdUrl = null;
    createdInviteId = null;
    try {
      const result = await createManagerInvite({
        teamId,
        request: {
          label: inviteLabel.trim() || undefined,
          expiresInDays: Number(expiresInDays),
          maxUses: 1,
        },
      });
      createdUrl = result.inviteUrl ?? null;
      createdInviteId = result.id ?? null;
      inviteLabel = "";
    } catch (err) {
      inviteError = describeSubmitError(
        err,
        "Could not make the link. Try again.",
        teamProblemMessages(),
      );
    } finally {
      inviting = false;
    }
  }

  let revokingId = $state<string | null>(null);
  let revokeError = $state<string | null>(null);

  async function revoke(inviteId: string) {
    revokingId = inviteId;
    revokeError = null;
    try {
      await revokeManagerInvite({ teamId, inviteId });
      if (inviteId === createdInviteId) {
        createdUrl = null;
        createdInviteId = null;
      }
    } catch (err) {
      revokeError = describeSubmitError(
        err,
        "Could not revoke the link. Try again.",
        teamProblemMessages(),
      );
    } finally {
      revokingId = null;
    }
  }

  let deleting = $state(false);
  let deleteError = $state<string | null>(null);

  async function remove() {
    deleting = true;
    deleteError = null;
    try {
      await deleteTeam(teamId);
      await goto(resolve("/(authenticated)/settings/teams"));
    } catch (err) {
      deleteError = describeSubmitError(
        err,
        "Could not delete the team. Try again.",
        teamProblemMessages(),
      );
    } finally {
      deleting = false;
    }
  }
</script>

<svelte:head>
  <title>{team?.name ?? "Team"} - Teams - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl space-y-6 p-3 @md:p-6">
  <Button variant="ghost" size="sm" href={resolve("/(authenticated)/settings/teams")} class="-ml-2">
    <ChevronLeft class="h-4 w-4" />
    Teams
  </Button>

  {#if refused}
    <TeamsUnavailable />
  {:else if leaving}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else if teamQuery?.error}
    <Alert.Root variant="destructive">
      <AlertTriangle class="h-4 w-4" />
      <Alert.Title>Could not load the team</Alert.Title>
      <Alert.Description>
        {remoteErrorMessage(teamQuery?.error, "Reload the page to try again.")}
      </Alert.Description>
    </Alert.Root>
  {:else if !team}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else}
    <div class="flex items-center gap-3">
      <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
        <UsersRound class="h-6 w-6 text-primary" />
      </div>
      <div class="min-w-0">
        <h1 class="truncate text-2xl font-bold tracking-tight">{team.name}</h1>
        <p class="text-muted-foreground">
          {#if isManager}
            The managers and staff of this team, the links that add them, and the sites that the team has access to.
          {:else if isStaff}
            You are staff of this team. These are its managers and your access.
          {:else}
            The managers of this team.
          {/if}
        </p>
      </div>
    </div>

    {#if isManager}
    <Card.Root>
      <Card.Header>
        <Card.Title>Name</Card.Title>
      </Card.Header>
      <Card.Content>
        <form
          class="space-y-4"
          onsubmit={(e) => {
            e.preventDefault();
            void rename();
          }}
        >
          <div class="space-y-2">
            <Label for="team-name">Team name</Label>
            <Input
              id="team-name"
              value={name}
              oninput={(e) => (renameDraft = e.currentTarget.value)}
              maxlength={teamNameMaxLength}
              autocomplete="off"
            />
          </div>
          {#if renameError}
            <p class="text-sm text-destructive" role="alert">{renameError}</p>
          {/if}
          <Button
            type="submit"
            disabled={renaming || name.trim() === "" || name.trim() === team.name}
          >
            {#if renaming}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            {/if}
            Rename team
          </Button>
        </form>
      </Card.Content>
    </Card.Root>
    {/if}

    <Card.Root>
      <Card.Header>
        <Card.Title>Managers</Card.Title>
        <Card.Description>
          A manager can rename the team, add and remove managers and staff, and delete the team.
        </Card.Description>
      </Card.Header>
      <Card.Content>
        {#if managerError}
          <p class="mb-3 text-sm text-destructive" role="alert">{managerError}</p>
        {/if}
        {#if !isManager}
          <ul class="divide-y">
            {#each team.managerNames ?? [] as managerName, i (i)}
              <li class="py-3">
                <span class="truncate font-medium">{managerName}</span>
              </li>
            {/each}
          </ul>
        {:else}
        <ul class="divide-y">
          {#each team.managers ?? [] as manager (manager.subjectId)}
            {@const isSelf = manager.subjectId === callerId}
            <li class="flex flex-col gap-2 py-3 @md:flex-row @md:items-center @md:justify-between">
              <div class="flex min-w-0 items-center gap-2">
                <span class="truncate font-medium">{manager.displayName}</span>
                {#if isSelf}
                  <Badge variant="secondary">You</Badge>
                {/if}
              </div>
              {#if isManager}
              <div class="flex flex-col gap-1 @md:items-end">
                {#if isSelf}
                  <ConfirmDialog
                    title="Leave team"
                    confirmLabel="Leave team"
                    destructive
                    onConfirm={() => removeOrLeave(manager.subjectId ?? "", true)}
                  >
                    {#snippet trigger(props)}
                      <Button
                        {...props}
                        variant="outline-destructive"
                        size="sm"
                        disabled={team.isLastManager || removingId !== null}
                        aria-describedby={team.isLastManager ? "leave-team-reason" : undefined}
                      >
                        {#if removingId === manager.subjectId}
                          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                        {:else}
                          <LogOut class="mr-2 h-4 w-4" />
                        {/if}
                        Leave
                      </Button>
                    {/snippet}
                    {#snippet description()}
                      Leave {team.name}? You lose access to the team. A manager of the team must add you again.
                    {/snippet}
                  </ConfirmDialog>
                  {#if team.isLastManager}
                    <p id="leave-team-reason" class="text-sm text-muted-foreground">
                      A team must keep one manager. Invite another manager before you leave.
                    </p>
                  {/if}
                {:else}
                  <ConfirmDialog
                    title="Remove manager"
                    confirmLabel="Remove manager"
                    destructive
                    onConfirm={() => removeOrLeave(manager.subjectId ?? "", false)}
                  >
                    {#snippet trigger(props)}
                      <Button
                        {...props}
                        variant="outline-destructive"
                        size="sm"
                        aria-label={`Remove ${manager.displayName}`}
                        disabled={removingId !== null}
                      >
                        {#if removingId === manager.subjectId}
                          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                        {:else}
                          <UserMinus class="mr-2 h-4 w-4" />
                        {/if}
                        Remove
                      </Button>
                    {/snippet}
                    {#snippet description()}
                      Remove {manager.displayName} as a manager of {team.name}? They lose access to the team. The links that they made stop working.
                    {/snippet}
                  </ConfirmDialog>
                {/if}
              </div>
              {/if}
            </li>
          {/each}
        </ul>
        {/if}
      </Card.Content>
    </Card.Root>

    {#if isStaff}
      <Card.Root>
        <Card.Header>
          <Card.Title>Your access</Card.Title>
          <Card.Description>
            {#if ownEnd}
              Your access through this team ended on {formatMediumDate(accessEndDate(ownEnd))}. You cannot manage the team.
            {:else}
              You see the sites that the team has access to and that let you in. You cannot manage the team.
            {/if}
          </Card.Description>
        </Card.Header>
        <Card.Content class="space-y-3">
          <p class="text-sm">
            <StaffNarrowing
              narrowing={team.ownStaff?.narrowing}
              roles={team.suggestableRoles ?? []}
              hasEnded={team.ownStaff?.hasEnded}
            />
          </p>
          {#if staffLeaveError}
            <p class="text-sm text-destructive" role="alert">{staffLeaveError}</p>
          {/if}
          <ConfirmDialog
            title="Leave team"
            confirmLabel="Leave team"
            destructive
            onConfirm={leaveAsStaff}
          >
            {#snippet trigger(props)}
              <Button {...props} variant="outline-destructive" size="sm" disabled={leaving || !callerId}>
                <LogOut class="mr-2 h-4 w-4" />
                Leave
              </Button>
            {/snippet}
            {#snippet description()}
              {#if ownEnd}
                Leave {team.name}? A manager of the team must invite you again.
              {:else}
                Leave {team.name}? You stop seeing the sites that the team has access to and that let you in. A manager of the team must invite you again.
              {/if}
            {/snippet}
          </ConfirmDialog>
        </Card.Content>
      </Card.Root>
    {/if}

    {#if isManager}
    <Card.Root>
      <Card.Header>
        <Card.Title>Invite a manager</Card.Title>
        <Card.Description>
          One person can use each link. A link stops working when the manager who made it leaves the team.
        </Card.Description>
      </Card.Header>
      <Card.Content class="space-y-5">
        <form
          class="space-y-5"
          onsubmit={(e) => {
            e.preventDefault();
            void createInvite();
          }}
        >
          <div class="space-y-2">
            <Label for="team-invite-label">Label (optional)</Label>
            <Input
              id="team-invite-label"
              bind:value={inviteLabel}
              maxlength={inviteLabelMaxLength}
              placeholder="For example, the name of the person"
            />
          </div>

          <div class="space-y-2">
            <Label id="team-invite-expiry-label">The link stops working after</Label>
            <RadioGroup.Root
              bind:value={expiresInDays}
              aria-labelledby="team-invite-expiry-label"
              class="flex flex-wrap gap-4"
            >
              {#each linkExpiryOptions() as [value, text] (value)}
                <div class="flex items-center gap-2">
                  <RadioGroup.Item {value} id={`team-invite-expiry-${value}`} />
                  <Label for={`team-invite-expiry-${value}`} variant="option">{text}</Label>
                </div>
              {/each}
            </RadioGroup.Root>
          </div>

          {#if inviteError}
            <p class="text-sm text-destructive" role="alert">{inviteError}</p>
          {/if}

          <Button type="submit" disabled={inviting}>
            {#if inviting}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            {/if}
            Make link
          </Button>
        </form>

        {#if createdUrl}
          <div class="space-y-2 rounded-lg border bg-muted/40 p-4" role="status">
            <p class="font-medium">Your link is ready</p>
            <div class="flex items-center gap-2">
              <Input readonly value={createdUrl} size="xs" class="font-mono" aria-label="Invite link" />
              <CopyButton text={createdUrl} label="Copy the invite link" />
            </div>
            <p class="text-sm text-muted-foreground">
              We show the link one time only. Copy it before you leave this page.
            </p>
          </div>
        {/if}

        <div class="space-y-2">
          <h2 class="font-medium">Links that work now</h2>
          {#if revokeError}
            <p class="text-sm text-destructive" role="alert">{revokeError}</p>
          {/if}
          {#if (team.invites ?? []).length === 0}
            <p class="text-sm text-muted-foreground">No link works now.</p>
          {:else}
            <ul class="divide-y">
              {#each team.invites ?? [] as invite (invite.id)}
                <li class="flex flex-col gap-2 py-3 @md:flex-row @md:items-center @md:justify-between">
                  <div class="space-y-1">
                    <p class="font-medium">{invite.label ?? "No label"}</p>
                    <p class="text-sm text-muted-foreground">
                      Made by {invite.createdByName} · Stops working {formatMediumDateTime(invite.expiresAt)}
                    </p>
                  </div>
                  <Button
                    variant="outline"
                    size="sm"
                    aria-label={`Revoke the link ${invite.label ?? "with no label"}`}
                    disabled={revokingId !== null}
                    onclick={() => revoke(invite.id ?? "")}
                  >
                    {#if revokingId === invite.id}
                      <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                    {/if}
                    Revoke
                  </Button>
                </li>
              {/each}
            </ul>
          {/if}
        </div>
      </Card.Content>
    </Card.Root>
    {/if}

    {#if isManager}
      <TeamStaffSection
        {teamId}
        teamName={team.name ?? ""}
        staff={team.staff ?? []}
        roles={team.suggestableRoles ?? []}
      />
      <StaffInvitesSection
        {teamId}
        invites={team.staffInvites ?? []}
        roles={team.suggestableRoles ?? []}
      />
      <AccessLinksSection
        {teamId}
        links={team.accessLinks ?? []}
        roles={team.suggestableRoles ?? []}
      />
      <TeamSitesSection sites={team.sites ?? []} />
    {/if}

    {#if isManager && team.canDelete}
      <Card.Root>
        <Card.Header>
          <Card.Title>Delete team</Card.Title>
          <Card.Description>
            Every manager and staff member loses the team. You cannot undo this.
          </Card.Description>
        </Card.Header>
        <Card.Content class="space-y-3">
          {#if deleteError}
            <p class="text-sm text-destructive" role="alert">{deleteError}</p>
          {/if}
          <ConfirmDialog
            title="Delete team"
            confirmLabel="Delete team"
            destructive
            busy={deleting}
            onConfirm={remove}
          >
            {#snippet trigger(props)}
              <Button {...props} variant="outline-destructive" disabled={deleting}>
                <Trash2 class="mr-2 h-4 w-4" />
                Delete team
              </Button>
            {/snippet}
            {#snippet description()}
              Delete {team.name}? Every manager and staff member loses the team. You cannot undo this.
            {/snippet}
          </ConfirmDialog>
        </Card.Content>
      </Card.Root>
    {/if}
  {/if}
</div>
