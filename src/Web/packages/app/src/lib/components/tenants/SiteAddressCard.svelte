<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Check from "@lucide/svelte/icons/check";
  import CopyButton from "#lib/components/members/CopyButton.svelte";
  import {
    getSubdomain,
    changeSubdomain,
  } from "#lib/api/generated/tenantSettings.generated.remote.js";
  import { validateSlug } from "#lib/api/generated/myTenants.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { formatNumericDate } from "#lib/utils/formatting.js";
  import { createSlugAvailability } from "./slug-availability.svelte.js";

  interface Props {
    /** When the site was created, as the settings endpoint reports it. */
    createdAt?: Date | string;
    /** The demo site refuses every change to its settings. */
    isDemo?: boolean;
  }

  let { createdAt, isDemo = false }: Props = $props();

  const subdomainQuery = getSubdomain();
  retainQuery(() => subdomainQuery);
  const subdomain = $derived(subdomainQuery.current);

  let open = $state(false);
  let newSlug = $state("");
  let understood = $state(false);
  let saving = $state(false);
  let saveError = $state<string | null>(null);

  const availability = createSlugAvailability(
    () => newSlug,
    (slug) => validateSlug({ slug }),
  );

  const newAddress = $derived(
    availability.normalized && subdomain?.baseDomain
      ? `https://${availability.normalized}.${subdomain.baseDomain}`
      : null,
  );

  const canSave = $derived(
    availability.current.state === "available" && understood && !saving,
  );

  function openDialog() {
    newSlug = "";
    understood = false;
    saveError = null;
    open = true;
  }

  async function save() {
    if (!canSave) return;
    saving = true;
    saveError = null;
    try {
      const result = await changeSubdomain({ slug: availability.normalized });
      // The session cookie covers every subdomain, so the browser stays signed in at the new address.
      if (result.address) {
        window.location.assign(`${result.address}/settings/site`);
        return;
      }
      open = false;
    } catch (err) {
      saveError = describeSubmitError(err, "Could not change the address. Try again.");
    } finally {
      saving = false;
    }
  }
</script>

<Card.Root>
  <Card.Header>
    <Card.Title>Address</Card.Title>
    <Card.Description>
      Use this address to sign in, to connect your apps, and to share with followers.
    </Card.Description>
  </Card.Header>
  <Card.Content class="space-y-4">
    {#if subdomain}
      <div class="flex items-center gap-2">
        <Input readonly value={subdomain.address ?? subdomain.slug} class="font-mono" />
        {#if subdomain.address}
          <CopyButton text={subdomain.address} label="Copy the site address" />
        {/if}
      </div>
    {:else}
      <Loader2 class="h-5 w-5 animate-spin text-muted-foreground" />
    {/if}

    <dl class="grid grid-cols-1 gap-4 @md:grid-cols-2">
      <div>
        <dt class="text-sm font-medium text-muted-foreground">Subdomain</dt>
        <dd class="mt-1 font-mono text-sm">{subdomain?.slug ?? "---"}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-muted-foreground">Created</dt>
        <dd class="mt-1 text-sm">
          {createdAt ? formatNumericDate(new Date(createdAt)) : "---"}
        </dd>
      </div>
    </dl>

    {#if subdomain?.canChange && !isDemo}
      <Button variant="outline" onclick={openDialog}>Change address</Button>
    {:else if subdomain}
      <p class="text-sm text-muted-foreground">
        You cannot change the address of this site.
      </p>
    {/if}
  </Card.Content>
</Card.Root>

<Dialog.Root bind:open>
  <Dialog.Content class="sm:max-w-lg">
    <Dialog.Header>
      <Dialog.Title>Change the site address</Dialog.Title>
      <Dialog.Description>
        The site moves to the new address at once. Nothing sends people from the old address to the new one.
      </Dialog.Description>
    </Dialog.Header>

    <Alert.Root variant="destructive">
      <AlertTriangle class="h-4 w-4" />
      <Alert.Title>WARNING: Apps that use the old address stop sending and showing data</Alert.Title>
      <Alert.Description>
        <p>After the change, put the new address into each of these:</p>
        <ul class="mt-2 list-disc space-y-1 pl-5">
          <li>Apps that upload data, for example xDrip+, AAPS, Loop, and Trio.</li>
          <li>Apps and watch faces that show your glucose to you or to followers.</li>
          <li>Bookmarks, and links that you gave to other people.</li>
        </ul>
        <p class="mt-2">Until you do this, these apps do not get or send data, and alarms in them do not sound.</p>
      </Alert.Description>
    </Alert.Root>

    <form
      class="space-y-4"
      onsubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <div class="space-y-2">
        <Label for="new-subdomain">New subdomain</Label>
        <Input
          id="new-subdomain"
          bind:value={newSlug}
          autocomplete="off"
          autocapitalize="off"
          spellcheck={false}
          class="font-mono"
          aria-invalid={availability.current.state === "refused"}
        />
        {#if newAddress}
          <p class="font-mono text-sm text-muted-foreground">{newAddress}</p>
        {/if}
        {#if availability.current.state === "checking"}
          <p class="text-sm text-muted-foreground">Checking the address...</p>
        {:else if availability.current.state === "refused"}
          <p class="text-sm text-destructive">{availability.current.message}</p>
        {:else if availability.current.state === "available"}
          <p class="flex items-center gap-1 text-sm text-success">
            <Check class="h-4 w-4" />
            This address is available.
          </p>
        {/if}
      </div>

      <div class="flex items-start gap-2">
        <Checkbox id="understood-subdomain-change" bind:checked={understood} />
        <Label for="understood-subdomain-change" variant="option">
          I understand that the old address stops working, and that I must change it in each app.
        </Label>
      </div>

      {#if saveError}
        <p class="text-sm text-destructive" role="alert">{saveError}</p>
      {/if}

      <Dialog.Footer>
        <Button type="button" variant="outline" onclick={() => (open = false)}>Cancel</Button>
        <Button type="submit" variant="destructive" disabled={!canSave}>
          {#if saving}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
          {/if}
          Change address
        </Button>
      </Dialog.Footer>
    </form>
  </Dialog.Content>
</Dialog.Root>
