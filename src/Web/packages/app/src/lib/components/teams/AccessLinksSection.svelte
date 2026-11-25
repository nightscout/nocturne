<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import CopyButton from "#lib/components/members/CopyButton.svelte";
  import AccessEndField from "#lib/components/members/AccessEndField.svelte";
  import PermissionList from "#lib/components/teams/PermissionList.svelte";
  import {
    accessEndDate,
    accessEndInstant,
    isAccessEndChoiceValid,
  } from "#lib/components/members/access-end.js";
  import { createAccessLink, revokeAccessLink } from "#lib/api/generated/teams.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { formatMediumDate, formatMediumDateTime } from "#lib/utils/formatting.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import { accessLinkLabelMaxLength } from "#lib/teams/limits.js";
  import { linkExpiryOptions } from "#lib/teams/link-expiry.svelte.js";
  import { teamProblemMessages } from "#lib/teams/problem-messages.svelte.js";
  import type { TeamAccessLinkSummary, TeamSuggestableRole } from "@nocturne/api-client";

  interface Props {
    teamId: string;
    links: TeamAccessLinkSummary[];
    roles: TeamSuggestableRole[];
  }

  let { teamId, links, roles }: Props = $props();

  const now = new Now(60_000);

  let label = $state("");
  let roleSlug = $state("");
  let limitTo24Hours = $state(false);
  let endDay = $state("");
  let expiresInDays = $state("7");
  let making = $state(false);
  let makeError = $state<string | null>(null);
  let createdUrl = $state<string | null>(null);
  let createdLinkId = $state<string | null>(null);

  const chosenRole = $derived(roles.find((r) => r.slug === roleSlug));
  const endIsOffered = $derived(isAccessEndChoiceValid(endDay, { now: now.current }));

  function roleName(slug: string | undefined): string | undefined {
    return roles.find((r) => r.slug === slug)?.name;
  }

  async function make() {
    if (making || !chosenRole?.slug) return;
    making = true;
    makeError = null;
    createdUrl = null;
    createdLinkId = null;
    try {
      const result = await createAccessLink({
        teamId,
        request: {
          label: label.trim() || undefined,
          roleSlug: chosenRole.slug,
          limitTo24Hours,
          accessEndsAt: endDay === "" ? undefined : accessEndInstant(endDay),
          expiresInDays: Number(expiresInDays),
        },
      });
      createdUrl = result.linkUrl ?? null;
      createdLinkId = result.id ?? null;
      label = "";
    } catch (err) {
      makeError = describeSubmitError(err, "Could not make the link. Try again.", teamProblemMessages());
    } finally {
      making = false;
    }
  }

  let revokingId = $state<string | null>(null);
  let revokeError = $state<string | null>(null);

  async function revoke(linkId: string) {
    revokingId = linkId;
    revokeError = null;
    try {
      await revokeAccessLink({ teamId, linkId });
      if (linkId === createdLinkId) {
        createdUrl = null;
        createdLinkId = null;
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
    <Card.Title>Request access</Card.Title>
    <Card.Description>
      A person opens the link and chooses the site to give access to. A link stops working when the
      manager who made it leaves the team.
    </Card.Description>
  </Card.Header>
  <Card.Content class="space-y-5">
    <form
      class="space-y-5"
      aria-label="Make an access link"
      onsubmit={(e) => {
        e.preventDefault();
        void make();
      }}
    >
      <div class="space-y-2">
        <Label for="team-access-label">Label of the access link (optional)</Label>
        <Input
          id="team-access-label"
          bind:value={label}
          maxlength={accessLinkLabelMaxLength}
          placeholder="For example, the name of the person who owns the site"
        />
      </div>

      <div class="space-y-2">
        <Label id="team-access-role-label">Role that the link suggests</Label>
        <RadioGroup.Root bind:value={roleSlug} aria-labelledby="team-access-role-label" class="gap-2">
          {#each roles as role (role.slug)}
            <RadioGroup.Card value={role.slug ?? ""}>
              <span class="text-sm font-medium">{role.name}</span>
            </RadioGroup.Card>
          {/each}
        </RadioGroup.Root>
        {#if chosenRole}
          <div class="space-y-1 rounded-lg border bg-muted/30 p-3">
            <p class="text-xs text-muted-foreground">What the team can see and change with this role</p>
            <PermissionList permissions={chosenRole.permissions ?? []} />
          </div>
        {:else}
          <p class="text-xs text-muted-foreground">Choose a role to make the link.</p>
        {/if}
      </div>

      <div class="flex items-start gap-2 rounded-md border bg-muted/30 p-3">
        <Checkbox
          id="team-access-limit"
          checked={limitTo24Hours}
          onCheckedChange={(checked: boolean) => (limitTo24Hours = checked === true)}
        />
        <div class="flex-1">
          <label for="team-access-limit" class="cursor-pointer text-sm font-medium select-none">
            Only the last 24 hours
          </label>
          <p class="mt-0.5 text-xs text-muted-foreground">
            The team can see only the most recent 24 hours of data.
          </p>
        </div>
      </div>

      <AccessEndField
        id="team-access-end"
        bind:value={endDay}
        now={now.current}
        label="Latest end of the access (optional)"
      />

      <div class="space-y-2">
        <Label id="team-access-expiry-label">The link stops working after</Label>
        <RadioGroup.Root
          bind:value={expiresInDays}
          aria-labelledby="team-access-expiry-label"
          class="flex flex-wrap gap-4"
        >
          {#each linkExpiryOptions() as [value, text] (value)}
            <div class="flex items-center gap-2">
              <RadioGroup.Item {value} id={`team-access-expiry-${value}`} />
              <Label for={`team-access-expiry-${value}`} variant="option">{text}</Label>
            </div>
          {/each}
        </RadioGroup.Root>
      </div>

      {#if makeError}
        <p class="text-sm text-destructive" role="alert">{makeError}</p>
      {/if}

      <Button type="submit" disabled={making || !chosenRole || !endIsOffered}>
        {#if making}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Make access link
      </Button>
    </form>

    {#if createdUrl}
      <div class="space-y-2 rounded-lg border bg-muted/40 p-4" role="status">
        <p class="font-medium">Your link is ready</p>
        <div class="flex items-center gap-2">
          <Input readonly value={createdUrl} size="xs" class="font-mono" aria-label="Access link" />
          <CopyButton text={createdUrl} label="Copy the access link" />
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
      {#if links.length === 0}
        <p class="text-sm text-muted-foreground">No access link works now.</p>
      {:else}
        <ul class="divide-y">
          {#each links as link (link.id)}
            <li class="flex flex-col gap-2 py-3 @md:flex-row @md:items-center @md:justify-between">
              <div class="space-y-1">
                <p class="font-medium">{link.label ?? "No label"}</p>
                <p class="text-sm text-muted-foreground">
                  {#if roleName(link.suggestion?.roleSlug)}
                    Suggests the role {roleName(link.suggestion?.roleSlug)}
                  {:else}
                    Suggests a role that is no longer offered
                  {/if}
                  {#if link.suggestion?.limitTo24Hours}
                    · Only the last 24 hours
                  {/if}
                  {#if link.suggestion?.accessEndsAt}
                    · Access ends by {formatMediumDate(accessEndDate(link.suggestion.accessEndsAt))}
                  {/if}
                </p>
                <p class="text-sm text-muted-foreground">
                  Made by {link.createdByName} · Stops working {formatMediumDateTime(link.expiresAt)}
                </p>
              </div>
              <Button
                variant="outline"
                size="sm"
                aria-label={`Revoke the link ${link.label ?? "with no label"}`}
                disabled={revokingId !== null}
                onclick={() => revoke(link.id ?? "")}
              >
                {#if revokingId === link.id}
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
