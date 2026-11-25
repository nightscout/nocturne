<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import CopyButton from "#lib/components/members/CopyButton.svelte";
  import StaffNarrowing from "#lib/components/teams/StaffNarrowing.svelte";
  import StaffNarrowingFields, {
    NO_CEILING,
    ceilingSlug,
  } from "#lib/components/teams/StaffNarrowingFields.svelte";
  import { accessEndInstant, isAccessEndChoiceValid } from "#lib/components/members/access-end.js";
  import { createStaffInvite, revokeStaffInvite } from "#lib/api/generated/teams.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { formatMediumDateTime } from "#lib/utils/formatting.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import { staffInviteLabelMaxLength } from "#lib/teams/limits.js";
  import { linkExpiryOptions } from "#lib/teams/link-expiry.svelte.js";
  import { teamProblemMessages } from "#lib/teams/problem-messages.svelte.js";
  import type { TeamStaffInviteSummary, TeamSuggestableRole } from "@nocturne/api-client";

  interface Props {
    teamId: string;
    invites: TeamStaffInviteSummary[];
    roles: TeamSuggestableRole[];
  }

  let { teamId, invites, roles }: Props = $props();

  const now = new Now(60_000);

  let label = $state("");
  let ceiling = $state(NO_CEILING);
  let limitTo24Hours = $state(false);
  let endDay = $state("");
  let expiresInDays = $state("7");
  let maxUses = $state("1");
  let making = $state(false);
  let makeError = $state<string | null>(null);
  let createdUrl = $state<string | null>(null);
  let createdInviteId = $state<string | null>(null);

  const endIsOffered = $derived(isAccessEndChoiceValid(endDay, { now: now.current }));

  async function make() {
    if (making || !endIsOffered) return;
    making = true;
    makeError = null;
    createdUrl = null;
    createdInviteId = null;
    try {
      const result = await createStaffInvite({
        teamId,
        request: {
          label: label.trim() || undefined,
          roleCeilingSlug: ceilingSlug(ceiling),
          limitTo24Hours,
          staffEndsAt: endDay === "" ? undefined : accessEndInstant(endDay),
          expiresInDays: Number(expiresInDays),
          maxUses: Number(maxUses),
        },
      });
      createdUrl = result.inviteUrl ?? null;
      createdInviteId = result.id ?? null;
      label = "";
    } catch (err) {
      makeError = describeSubmitError(err, "Could not make the link. Try again.", teamProblemMessages());
    } finally {
      making = false;
    }
  }

  let revokingId = $state<string | null>(null);
  let revokeError = $state<string | null>(null);

  async function revoke(inviteId: string) {
    revokingId = inviteId;
    revokeError = null;
    try {
      await revokeStaffInvite({ teamId, inviteId });
      if (inviteId === createdInviteId) {
        createdUrl = null;
        createdInviteId = null;
      }
    } catch (err) {
      revokeError = describeSubmitError(err, "Could not revoke the link. Try again.", teamProblemMessages());
    } finally {
      revokingId = null;
    }
  }
</script>

<Card.Root>
  <Card.Header>
    <Card.Title>Invite staff</Card.Title>
    <Card.Description>
      A staff member works for the team and sees the sites that the team has access to and that let
      them in. A staff member cannot manage the team. A link stops working when the manager who made it leaves the team.
    </Card.Description>
  </Card.Header>
  <Card.Content class="space-y-5">
    <form
      class="space-y-5"
      aria-label="Make a staff link"
      onsubmit={(e) => {
        e.preventDefault();
        void make();
      }}
    >
      <div class="space-y-2">
        <Label for="team-staff-invite-label">Label of the staff link (optional)</Label>
        <Input
          id="team-staff-invite-label"
          bind:value={label}
          maxlength={staffInviteLabelMaxLength}
          placeholder="For example, the name of the person"
        />
      </div>

      <StaffNarrowingFields
        id="team-staff-invite"
        {roles}
        bind:ceiling
        bind:limitTo24Hours
        bind:endDay
        now={now.current}
        disabled={making}
      />

      <div class="space-y-2">
        <Label id="team-staff-invite-uses-label">Number of people who can use the link</Label>
        <RadioGroup.Root
          bind:value={maxUses}
          aria-labelledby="team-staff-invite-uses-label"
          class="flex flex-wrap gap-4"
        >
          <div class="flex items-center gap-2">
            <RadioGroup.Item value="1" id="team-staff-invite-uses-1" />
            <Label for="team-staff-invite-uses-1" variant="option">1 person</Label>
          </div>
          <div class="flex items-center gap-2">
            <RadioGroup.Item value="10" id="team-staff-invite-uses-10" />
            <Label for="team-staff-invite-uses-10" variant="option">10 people</Label>
          </div>
          <div class="flex items-center gap-2">
            <RadioGroup.Item value="50" id="team-staff-invite-uses-50" />
            <Label for="team-staff-invite-uses-50" variant="option">50 people</Label>
          </div>
        </RadioGroup.Root>
      </div>

      <div class="space-y-2">
        <Label id="team-staff-invite-expiry-label">The link stops working after</Label>
        <RadioGroup.Root
          bind:value={expiresInDays}
          aria-labelledby="team-staff-invite-expiry-label"
          class="flex flex-wrap gap-4"
        >
          {#each linkExpiryOptions() as [value, text] (value)}
            <div class="flex items-center gap-2">
              <RadioGroup.Item {value} id={`team-staff-invite-expiry-${value}`} />
              <Label for={`team-staff-invite-expiry-${value}`} variant="option">{text}</Label>
            </div>
          {/each}
        </RadioGroup.Root>
      </div>

      {#if makeError}
        <p class="text-sm text-destructive" role="alert">{makeError}</p>
      {/if}

      <Button type="submit" disabled={making || !endIsOffered}>
        {#if making}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Make staff link
      </Button>
    </form>

    {#if createdUrl}
      <div class="space-y-2 rounded-lg border bg-muted/40 p-4" role="status">
        <p class="font-medium">Your link is ready</p>
        <div class="flex items-center gap-2">
          <Input readonly value={createdUrl} size="xs" class="font-mono" aria-label="Staff link" />
          <CopyButton text={createdUrl} label="Copy the staff link" />
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
      {#if invites.length === 0}
        <p class="text-sm text-muted-foreground">No staff link works now.</p>
      {:else}
        <ul class="divide-y">
          {#each invites as invite (invite.id)}
            <li class="flex flex-col gap-2 py-3 @md:flex-row @md:items-center @md:justify-between">
              <div class="space-y-1">
                <p class="font-medium">{invite.label ?? "No label"}</p>
                <p class="text-sm text-muted-foreground">
                  <StaffNarrowing narrowing={invite.narrowing} {roles} />
                </p>
                <p class="text-sm text-muted-foreground">
                  {#if invite.maxUses}
                    Uses: {invite.useCount ?? 0} of {invite.maxUses}
                  {:else}
                    Uses: {invite.useCount ?? 0}, no limit
                  {/if}
                  · Made by {invite.createdByName} · Stops working {formatMediumDateTime(invite.expiresAt)}
                </p>
              </div>
              <Button
                variant="outline"
                size="sm"
                aria-label={`Revoke the staff link ${invite.label ?? "with no label"}`}
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
