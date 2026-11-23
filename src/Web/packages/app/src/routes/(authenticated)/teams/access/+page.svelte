<script lang="ts">
  import { page } from "$app/state";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Check from "@lucide/svelte/icons/check";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import ShieldCheck from "@lucide/svelte/icons/shield-check";
  import { approveAccessLink, getAccessLinkInfo } from "#lib/api/generated/teams.generated.remote.js";
  import { getMyTenants } from "#lib/api/generated/myTenants.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import {
    READ_SURFACE,
    describeRemoteError,
    errorStatus,
    isSignInRequired,
  } from "#lib/forms/submit-error.js";
  import { FormError } from "#lib/forms/index.js";
  import { goToTenant } from "#lib/utils/tenant-host.js";
  import { teamAccessProblemMessages } from "#lib/teams/problem-messages.svelte.js";
  import TeamsUnavailable from "#lib/components/teams/TeamsUnavailable.svelte";
  import AccessApprovalForm from "#lib/components/teams/AccessApprovalForm.svelte";
  import type { ApproveTeamAccessRequest } from "@nocturne/api-client";

  const token = $derived(page.url.searchParams.get("token") ?? "");

  const refused = $derived(page.data.refusedAsDemoSubject === true);
  const linkQuery = $derived(token && !refused ? getAccessLinkInfo(token) : undefined);
  retainQuery(() => linkQuery);
  const link = $derived(linkQuery?.current);

  const notValid = () => "This link is not valid. Ask the manager who sent it for a new link.";

  const unusableReason = $derived.by(() => {
    if (!token) return "This link has no access token. Check the link that you were given.";
    const err = linkQuery?.error;
    if (!err) return null;
    if (errorStatus(err) === 404) return notValid();
    if (errorStatus(err) === 403 && !isSignInRequired(err)) return "Your sign-in cannot give a team access.";
    return describeRemoteError(err, "This link cannot be used.", READ_SURFACE, teamAccessProblemMessages());
  });

  let approving = $state(false);
  let approveError = $state<string | null>(null);
  let given = $state<{ teamName: string; siteName: string } | null>(null);
  let frozen = $state<typeof link>(undefined);

  // The lookup refreshes once an approval lands and may then refuse; the page keeps what the person approved.
  const view = $derived(approving ? frozen : link);
  const showForm = $derived(!refused && (approving ? !!frozen : !given && !unusableReason && !!view));

  const sites = $derived(view?.sites ?? []);
  const selectable = $derived(sites.filter((s) => !s.alreadyGiven));
  let pickedTenantId = $state<string | null>(null);
  const chosenTenantId = $derived(
    pickedTenantId ?? (selectable.length === 1 ? selectable[0]!.tenantId : undefined),
  );
  const chosenSite = $derived(selectable.find((s) => s.tenantId === chosenTenantId));

  async function approve(request: ApproveTeamAccessRequest) {
    const teamName = view?.teamName ?? "";
    const siteName = chosenSite?.displayName ?? "";
    frozen = link;
    approving = true;
    approveError = null;
    let leaving = false;
    try {
      const result = await approveAccessLink({ token, request });
      given = { teamName, siteName };
      leaving = await openSiteMembers(result.tenantId);
    } catch (err) {
      approveError = describeRemoteError(
        err,
        "Could not give the team access. Try again.",
        { missing: notValid, forbiddenReason: true, serverError: false },
        teamAccessProblemMessages(),
      );
    } finally {
      if (!leaving) approving = false;
    }
  }

  async function openSiteMembers(tenantId: string | undefined): Promise<boolean> {
    const baseDomain = page.data.baseDomain;
    if (!tenantId || !baseDomain) return false;
    try {
      const slug = (await getMyTenants({ reachableOnly: true })).find((t) => t.id === tenantId)?.slug;
      if (!slug) return false;
      goToTenant(slug, baseDomain, "settings/members");
      return true;
    } catch {
      // The access is given; only the way to its site is unknown, and the done state says so.
      return false;
    }
  }
</script>

<svelte:head>
  <title>Give a team access - Nocturne</title>
</svelte:head>

<div class="flex flex-1 items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    {#if refused}
      <Card.Header class="space-y-1 text-center">
        <Card.Title class="text-2xl font-bold">Give a team access</Card.Title>
        <Card.Description><TeamsUnavailable /></Card.Description>
      </Card.Header>
    {:else if given && !approving}
      <Card.Header class="space-y-1 text-center">
        <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
          <Check class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">The team has access</Card.Title>
        <Card.Description>
          The team <strong>{given.teamName}</strong> now has access to <strong>{given.siteName}</strong>.
        </Card.Description>
      </Card.Header>
    {:else if showForm && view}
      {#if approving}
        <Card.Header class="space-y-1 text-center">
          <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
            <Loader2 class="h-6 w-6 animate-spin text-primary" />
          </div>
          <Card.Title class="text-2xl font-bold">Giving access...</Card.Title>
        </Card.Header>
      {/if}
      <div class={approving ? "hidden" : ""}>
        <Card.Header class="space-y-2 text-center">
          <div class="mx-auto mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
            <ShieldCheck class="h-6 w-6 text-primary" />
          </div>
          <Card.Title class="text-2xl font-bold">Give a team access</Card.Title>
          <Card.Description>
            <strong>{view.createdByName}</strong> asks you to give the team <strong>{view.teamName}</strong>
            access to a site.
          </Card.Description>
          <p class="pb-2 text-sm text-muted-foreground">
            Nocturne does not check who runs a team. Give access only if you know {view.createdByName}.
          </p>
        </Card.Header>
        <Card.Content class="space-y-5">
          {#if sites.length === 0}
            <p class="text-sm text-muted-foreground">
              Only an owner of a site can give a team access. You do not own a site.
            </p>
          {:else}
            <RadioGroup.Root
              class="gap-2"
              aria-label="Site to give access to"
              value={chosenTenantId}
              onValueChange={(value) => (pickedTenantId = value)}
            >
              {#each sites as site (site.tenantId)}
                {#if site.alreadyGiven}
                  <div class="rounded-md border p-3">
                    <p class="text-sm font-medium">{site.displayName}</p>
                    <p class="text-xs text-muted-foreground">This team already has access to this site.</p>
                  </div>
                {:else}
                  <RadioGroup.Card value={site.tenantId ?? ""}>
                    <span class="text-sm font-medium">{site.displayName}</span>
                  </RadioGroup.Card>
                {/if}
              {/each}
            </RadioGroup.Root>

            {#if chosenSite}
              {#key chosenSite.tenantId}
                <AccessApprovalForm site={chosenSite} onApprove={approve} />
              {/key}
            {/if}
          {/if}
          <FormError issues={approveError} focusOnShow />
        </Card.Content>
      </div>
    {:else if unusableReason}
      <Card.Header class="space-y-1 text-center">
        <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10">
          <AlertTriangle class="h-6 w-6 text-destructive" />
        </div>
        <Card.Title class="text-2xl font-bold">Link unavailable</Card.Title>
        <Card.Description>{unusableReason}</Card.Description>
      </Card.Header>
    {:else}
      <Card.Header class="space-y-1 text-center">
        <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
          <Loader2 class="h-6 w-6 animate-spin text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">Loading link...</Card.Title>
      </Card.Header>
    {/if}
  </Card.Root>
</div>
