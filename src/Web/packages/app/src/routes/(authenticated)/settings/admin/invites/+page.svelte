<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Link2 from "@lucide/svelte/icons/link-2";
  import MailPlus from "@lucide/svelte/icons/mail-plus";
  import Globe from "@lucide/svelte/icons/globe";
  import UserRound from "@lucide/svelte/icons/user-round";
  import KeyRound from "@lucide/svelte/icons/key-round";
  import CopyButton from "#lib/components/members/CopyButton.svelte";
  import {
    getOverview,
    create,
    revoke,
  } from "#lib/api/generated/tenantCreationInviteAdmins.generated.remote.js";
  import {
    getPolicy,
    setPolicy,
  } from "#lib/api/generated/platformPolicies.generated.remote.js";
  import {
    TenantCreationInviteStatus,
    type TenantCreationInviteDto,
  } from "#lib/api/index.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { formatNumericDate } from "#lib/utils/formatting.js";

  const overviewQuery = getOverview();
  retainQuery(() => overviewQuery);
  const overview = $derived(overviewQuery.current);
  const notice = $derived(overview?.subdomainNotice);
  const baseDomain = $derived(overview?.baseDomain);

  const policyQuery = getPolicy();
  const policy = $derived(policyQuery.current);
  let policySaving = $state(false);
  let policyError = $state<string | null>(null);

  async function setOwnerSlugChange(allow: boolean) {
    policySaving = true;
    policyError = null;
    try {
      await setPolicy({ allowOwnerSlugChange: allow });
    } catch (err) {
      policyError = describeSubmitError(err, "Could not save the setting. Try again.");
    } finally {
      policySaving = false;
    }
  }

  // ── New link ──────────────────────────────────────────────────────
  let label = $state("");
  let usage = $state<"single" | "multiple">("single");
  // A number input binds a number, or undefined (null in some browsers) while it is empty.
  let maxUses = $state<number | null | undefined>(undefined);
  let expiresInDays = $state("7");
  let confirmed = $state(false);
  let creating = $state(false);
  let createError = $state<string | null>(null);
  let createdUrl = $state<string | null>(null);

  const parsedMaxUses = $derived(maxUses ?? null);
  const maxUsesInvalid = $derived(
    usage === "multiple" &&
      parsedMaxUses !== null &&
      (!Number.isInteger(parsedMaxUses) || parsedMaxUses < 2),
  );
  const canCreate = $derived(
    !!baseDomain && !creating && !maxUsesInvalid && (!notice || confirmed),
  );

  async function createLink() {
    if (!canCreate) return;
    creating = true;
    createError = null;
    createdUrl = null;
    try {
      const result = await create({
        label: label.trim() || undefined,
        maxUses: usage === "single" ? 1 : (parsedMaxUses ?? undefined),
        expiresInDays: Number(expiresInDays),
        confirmSubdomainChange: confirmed,
      });
      createdUrl = result.url ?? null;
      label = "";
      maxUses = undefined;
      confirmed = false;
    } catch (err) {
      createError = describeSubmitError(err, "Could not make the link. Try again.");
    } finally {
      creating = false;
    }
  }

  let revokingId = $state<string | null>(null);
  let revokeError = $state<string | null>(null);

  async function revokeLink(invite: TenantCreationInviteDto) {
    if (!invite.id) return;
    revokingId = invite.id;
    revokeError = null;
    try {
      await revoke(invite.id);
    } catch (err) {
      revokeError = describeSubmitError(err, "Could not cancel the link. Try again.");
    } finally {
      revokingId = null;
    }
  }

  function statusLabel(status: TenantCreationInviteStatus | undefined): string {
    switch (status) {
      case TenantCreationInviteStatus.Active:
        return "Active";
      case TenantCreationInviteStatus.Revoked:
        return "Canceled";
      case TenantCreationInviteStatus.Expired:
        return "Expired";
      case TenantCreationInviteStatus.UsedUp:
        return "Used";
      default:
        return "";
    }
  }

  function usesLabel(invite: TenantCreationInviteDto): string {
    const used = invite.useCount ?? 0;
    return invite.maxUses == null ? `${used} used, no limit` : `${used} of ${invite.maxUses} used`;
  }
</script>

<svelte:head>
  <title>Invite people - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl space-y-6 p-3 @md:p-6">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
      <MailPlus class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Invite people</h1>
      <p class="text-muted-foreground">
        Give people a link to make their own Nocturne site on this server
      </p>
    </div>
  </div>

  {#if overviewQuery.error}
    <Alert.Root variant="destructive">
      <AlertTriangle class="h-4 w-4" />
      <Alert.Title>Could not load the invite links</Alert.Title>
      <Alert.Description>
        {remoteErrorMessage(overviewQuery.error, "Reload the page to try again.")}
      </Alert.Description>
    </Alert.Root>
  {:else if !overview}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else}
    <Card.Root>
      <Card.Header>
        <Card.Title>How an invite works</Card.Title>
      </Card.Header>
      <Card.Content>
        <ol class="grid gap-4 @md:grid-cols-3">
          <li class="flex gap-3">
            <Link2 class="mt-0.5 h-5 w-5 shrink-0 text-primary" />
            <div class="space-y-1">
              <p class="font-medium">1. You make a link</p>
              <p class="text-sm text-muted-foreground">
                Send it to the person by email or chat. A link can work for one person or for many people.
              </p>
            </div>
          </li>
          <li class="flex gap-3">
            <KeyRound class="mt-0.5 h-5 w-5 shrink-0 text-primary" />
            <div class="space-y-1">
              <p class="font-medium">2. They make an account</p>
              <p class="text-sm text-muted-foreground">
                They select an address for their site, then sign in with a passkey. They do not need a password.
              </p>
            </div>
          </li>
          <li class="flex gap-3">
            <Globe class="mt-0.5 h-5 w-5 shrink-0 text-primary" />
            <div class="space-y-1">
              <p class="font-medium">3. They get their own site</p>
              <p class="text-sm text-muted-foreground">
                The site has its own address, for example <span class="font-mono">name.{baseDomain ?? "example.com"}</span>. Their data is in their site only.
              </p>
            </div>
          </li>
        </ol>
        <p class="mt-4 text-sm text-muted-foreground">
          They own their site. They decide who can see their data. As a server administrator, you can open their site for support, and you can turn it off or delete it. Nocturne records each time that an administrator opens a site.
        </p>
      </Card.Content>
    </Card.Root>

    {#if !baseDomain}
      <Alert.Root variant="destructive">
        <AlertTriangle class="h-4 w-4" />
        <Alert.Title>Set a base domain first</Alert.Title>
        <Alert.Description>
          Each site needs its own subdomain. Set BASE_DOMAIN on the server, then restart it.
        </Alert.Description>
      </Alert.Root>
    {/if}

    {#if notice}
      <Alert.Root variant="destructive">
        <AlertTriangle class="h-4 w-4" />
        <Alert.Title>WARNING: Move your apps to the site address before you invite people</Alert.Title>
        <Alert.Description>
          <div class="flex flex-col gap-3">
          <p>
            Now, <span class="font-mono">{notice.bareAddress}</span> opens
            <strong>{notice.tenantDisplayName}</strong>, because it is the only site on this server.
          </p>
          <p>
            When a second site exists, <span class="font-mono">{notice.bareAddress}</span> does not open a site. Apps that use it stop sending and showing data, and alarms in them do not sound.
          </p>
          <div class="flex items-center gap-2">
            <Input readonly value={notice.tenantAddress} class="font-mono" />
            {#if notice.tenantAddress}
              <CopyButton text={notice.tenantAddress} label="Copy the site address" />
            {/if}
          </div>
          <p>Before you make the first link, put this address into each of these:</p>
          <ul class="list-disc space-y-1 pl-5">
            <li>Apps that upload data, for example xDrip+, AAPS, Loop, and Trio.</li>
            <li>Apps and watch faces that show glucose to you or to followers.</li>
            <li>Bookmarks, and links that you gave to other people.</li>
          </ul>
          </div>
        </Alert.Description>
      </Alert.Root>
    {/if}

    <Card.Root>
      <Card.Header>
        <Card.Title>Make a link</Card.Title>
        <Card.Description>We show the link one time only. Copy it before you leave this page.</Card.Description>
      </Card.Header>
      <Card.Content>
        <form
          class="space-y-5"
          onsubmit={(e) => {
            e.preventDefault();
            void createLink();
          }}
        >
          <div class="space-y-2">
            <Label for="invite-label">Label (optional)</Label>
            <Input id="invite-label" bind:value={label} maxlength={255} placeholder="For example, the name of the person" />
          </div>

          <div class="space-y-2">
            <Label>Who can use the link</Label>
            <RadioGroup.Root bind:value={usage}>
              <div class="flex items-center gap-2">
                <RadioGroup.Item value="single" id="usage-single" />
                <Label for="usage-single" variant="option">One person</Label>
              </div>
              <div class="flex items-center gap-2">
                <RadioGroup.Item value="multiple" id="usage-multiple" />
                <Label for="usage-multiple" variant="option">More than one person</Label>
              </div>
            </RadioGroup.Root>
            {#if usage === "multiple"}
              <div class="space-y-1 pl-6">
                <Label for="invite-max-uses" variant="option">Maximum number of sites (optional)</Label>
                <Input
                  id="invite-max-uses"
                  type="number"
                  min={2}
                  max={1000}
                  bind:value={maxUses}
                  class="w-32"
                  aria-invalid={maxUsesInvalid}
                />
                {#if maxUsesInvalid}
                  <p class="text-sm text-destructive">Enter a whole number of 2 or more.</p>
                {:else}
                  <p class="text-sm text-muted-foreground">If you leave this empty, the link has no limit.</p>
                {/if}
              </div>
            {/if}
          </div>

          <div class="space-y-2">
            <Label>The link stops working after</Label>
            <RadioGroup.Root bind:value={expiresInDays} class="flex flex-wrap gap-4">
              {#each [["1", "1 day"], ["7", "7 days"], ["30", "30 days"], ["90", "90 days"]] as [value, text] (value)}
                <div class="flex items-center gap-2">
                  <RadioGroup.Item {value} id={`expiry-${value}`} />
                  <Label for={`expiry-${value}`} variant="option">{text}</Label>
                </div>
              {/each}
            </RadioGroup.Root>
          </div>

          {#if notice}
            <div class="flex items-start gap-2">
              <Checkbox id="confirm-subdomain" bind:checked={confirmed} />
              <Label for="confirm-subdomain" variant="option">
                I put <span class="font-mono">{notice.tenantAddress}</span> into each app, watch face, and bookmark.
              </Label>
            </div>
          {/if}

          {#if createError}
            <p class="text-sm text-destructive" role="alert">{createError}</p>
          {/if}

          <Button type="submit" disabled={!canCreate}>
            {#if creating}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            {/if}
            Make link
          </Button>
        </form>

        {#if createdUrl}
          <div class="mt-5 space-y-2 rounded-lg border bg-muted/40 p-4" role="status">
            <p class="font-medium">Your link is ready</p>
            <div class="flex items-center gap-2">
              <Input readonly value={createdUrl} size="xs" class="font-mono" />
              <CopyButton text={createdUrl} label="Copy the invite link" />
            </div>
            <p class="text-sm text-muted-foreground">
              Send this link to the person. The link shows them how their site address works.
            </p>
          </div>
        {/if}
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <Card.Title>Links</Card.Title>
      </Card.Header>
      <Card.Content>
        {#if revokeError}
          <p class="mb-3 text-sm text-destructive" role="alert">{revokeError}</p>
        {/if}
        {#if (overview.invites ?? []).length === 0}
          <p class="text-sm text-muted-foreground">No links yet. Make a link to invite a person.</p>
        {:else}
          <ul class="divide-y">
            {#each overview.invites ?? [] as invite (invite.id)}
              <li class="flex flex-col gap-2 py-3 @md:flex-row @md:items-start @md:justify-between">
                <div class="space-y-1">
                  <div class="flex items-center gap-2">
                    <span class="font-medium">{invite.label ?? "No label"}</span>
                    <Badge variant={invite.status === TenantCreationInviteStatus.Active ? "default" : "secondary"}>
                      {statusLabel(invite.status)}
                    </Badge>
                  </div>
                  <p class="text-sm text-muted-foreground">
                    {usesLabel(invite)} · Stops working {invite.expiresAt ? formatNumericDate(new Date(invite.expiresAt)) : "---"}
                  </p>
                  {#each invite.uses ?? [] as use (use.redeemedAt)}
                    <p class="flex items-center gap-1 text-sm">
                      <UserRound class="h-3.5 w-3.5 text-muted-foreground" />
                      {#if use.address}
                        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- another site's origin -->
                        <a class="underline" href={use.address} target="_blank" rel="noreferrer">{use.displayName}</a>
                      {:else}
                        <span class="text-muted-foreground">Deleted site</span>
                      {/if}
                    </p>
                  {/each}
                </div>
                {#if invite.status === TenantCreationInviteStatus.Active}
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={revokingId === invite.id}
                    onclick={() => revokeLink(invite)}
                  >
                    {#if revokingId === invite.id}
                      <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                    {/if}
                    Cancel link
                  </Button>
                {/if}
              </li>
            {/each}
          </ul>
        {/if}
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <Card.Title>Site addresses</Card.Title>
      </Card.Header>
      <Card.Content class="space-y-2">
        <div class="flex items-start justify-between gap-4">
          <div class="space-y-1">
            <Label for="allow-owner-slug-change">Owners can change the address of their site</Label>
            <p class="text-sm text-muted-foreground">
              When the address changes, apps that use the old address stop working. Server administrators can always change the address of a site that they own.
            </p>
          </div>
          <Switch
            id="allow-owner-slug-change"
            checked={policy?.allowOwnerSlugChange ?? false}
            disabled={!policy || policySaving}
            onCheckedChange={(checked) => setOwnerSlugChange(checked)}
          />
        </div>
        {#if policyError}
          <p class="text-sm text-destructive" role="alert">{policyError}</p>
        {/if}
      </Card.Content>
    </Card.Root>
  {/if}
</div>
