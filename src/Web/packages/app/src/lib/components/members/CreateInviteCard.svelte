<script module lang="ts">
  /** An access level in plain words, granting one role. */
  export interface InviteRoleChoice {
    roleId: string;
    label: string;
    description: string;
  }
</script>

<script lang="ts">
  import SuccessBanner from "#lib/forms/SuccessBanner.svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import { Select, SelectContent, SelectItem, SelectTrigger } from "#lib/components/ui/select/index.js";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import ChevronUp from "@lucide/svelte/icons/chevron-up";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import PermissionCategorySelector from "#lib/components/rbac/PermissionCategorySelector.svelte";
  import CopyInvitationMessageButton from "#lib/components/members/CopyInvitationMessageButton.svelte";
  import CopyButton from "#lib/components/members/CopyButton.svelte";
  import { Artwork } from "water-ui";
  import { createInvite } from "#lib/api/generated/memberInvites.generated.remote.js";
  import type { TenantRoleDto } from "#lib/api/generated/nocturne-api-client.js";
  import { describeSubmitError, FormError } from "#lib/forms/index.js";

  interface Props {
    roles: TenantRoleDto[];
    /**
     * Offered as a single choice in place of the role checkboxes and direct permissions, for
     * someone who should not need to know the roles by name. Empty falls back to the checkboxes.
     */
    roleChoices?: InviteRoleChoice[];
    /** The link lets in one person, with no choice to allow more. */
    singleUseOnly?: boolean;
    /** Offers the 24-hour limit. Off for a role the server never clamps, which would refuse it. */
    offerHistoryLimit?: boolean;
    onCreated?: (url: string) => void;
    onCancel: () => void;
  }

  let {
    roles = [],
    roleChoices,
    singleUseOnly = false,
    offerHistoryLimit = true,
    onCreated,
    onCancel,
  }: Props = $props();

  const grantableRoles = $derived(roles.filter((r) => r.canGrant));
  const grantableChoices = $derived(
    (roleChoices ?? []).filter((c) => grantableRoles.some((r) => r.id === c.roleId)),
  );

  // The API refuses anything past 90 days: the token is a bearer credential for tenant
  // membership, so its lifetime is bounded rather than free-form.
  const EXPIRY_OPTIONS = [
    { value: 1, label: "1 day" },
    { value: 7, label: "7 days" },
    { value: 14, label: "14 days" },
    { value: 30, label: "30 days" },
    { value: 90, label: "90 days" },
  ];

  let expiresInDays = $state(7);
  const expiryLabel = $derived(
    EXPIRY_OPTIONS.find((o) => o.value === expiresInDays)?.label ?? `${expiresInDays} days`,
  );
  let inviteLabel = $state("");
  let chosenRoleIds = $state<string[]>([]);
  /** A single choice is the answer already, so it needs no click. */
  const inviteRoleIds = $derived(
    chosenRoleIds.length === 0 && grantableChoices.length === 1
      ? [grantableChoices[0].roleId]
      : chosenRoleIds,
  );
  let inviteDirectPermissions = $state<string[]>([]);
  let showInvitePermissions = $state(false);
  let allowMultipleUses = $state(false);
  let limitTo24Hours = $state(false);
  let createdInviteUrl = $state<string | null>(null);
  let createdByName = $state<string | undefined>(undefined);
  let isCreatingInvite = $state(false);
  let errorMessage = $state<string | null>(null);

  function toggleInviteRole(roleId: string) {
    if (chosenRoleIds.includes(roleId)) {
      chosenRoleIds = chosenRoleIds.filter((r) => r !== roleId);
    } else {
      chosenRoleIds = [...chosenRoleIds, roleId];
    }
  }

  async function handleCreateInvite() {
    isCreatingInvite = true;
    errorMessage = null;
    try {
      const result = await createInvite({
        roleIds: inviteRoleIds.length > 0 ? inviteRoleIds : undefined,
        directPermissions:
          inviteDirectPermissions.length > 0
            ? inviteDirectPermissions
            : undefined,
        label: inviteLabel || undefined,
        expiresInDays,
        maxUses: allowMultipleUses ? undefined : 1,
        limitTo24Hours,
      });
      if (result.inviteUrl) {
        createdByName = result.createdByName;
        createdInviteUrl = result.inviteUrl.startsWith("http")
          ? result.inviteUrl
          : `${window.location.origin}${result.inviteUrl}`;
        onCreated?.(createdInviteUrl);
      }
    } catch (e) {
      errorMessage = describeSubmitError(e, "Failed to create invite. Try again.");
    } finally {
      isCreatingInvite = false;
    }
  }

  function handleDone() {
    resetForm();
    onCancel();
  }

  function handleCancel() {
    resetForm();
    onCancel();
  }

  function resetForm() {
    expiresInDays = 7;
    inviteLabel = "";
    chosenRoleIds = [];
    inviteDirectPermissions = [];
    showInvitePermissions = false;
    allowMultipleUses = false;
    limitTo24Hours = false;
    createdInviteUrl = null;
    createdByName = undefined;
    errorMessage = null;
  }
</script>

<Card.Root data-testid="create-invite-card">
  <Card.Header>
    <Card.Title class="text-lg">Create invite link</Card.Title>
    <Card.Description>
      Generate a shareable link. Anyone with this link can accept the invite
      after signing in.
    </Card.Description>
  </Card.Header>
  <Card.Content class="@container">
    {#if createdInviteUrl}
      <div class="space-y-4">
        <Artwork
          artwork="people-group"
          palette="dusk"
          motion="auto"
          autoplay="once"
          class="mx-auto size-32"
        />
        <SuccessBanner>Invite link created. Share it with the new member.</SuccessBanner>

        <div class="flex gap-2">
          <Input
            type="text"
            value={createdInviteUrl}
            readonly
            class="font-mono"
          />
          <CopyButton
            text={createdInviteUrl}
            label="Copy invite link"
            oncopied={() => (errorMessage = null)}
          />
        </div>

        <CopyInvitationMessageButton
          url={createdInviteUrl}
          inviterName={createdByName}
        />

        <Button variant="outline" class="w-full" onclick={handleDone}>
          Done
        </Button>
      </div>
    {:else}
      <div class="space-y-4">
        <div class="space-y-2">
          <Label for="invite-label">Label (optional)</Label>
          <Input
            id="invite-label"
            type="text"
            placeholder="e.g. Mom, Endocrinologist"
            bind:value={inviteLabel}
          />
        </div>

        {#if grantableChoices.length}
          <div class="space-y-2">
            <Label id="invite-access-label">What can they do?</Label>
            <RadioGroup.Root
              class="gap-2"
              aria-labelledby="invite-access-label"
              value={inviteRoleIds[0]}
              onValueChange={(value) => (chosenRoleIds = [value])}
            >
              {#each grantableChoices as choice (choice.roleId)}
                <RadioGroup.Card value={choice.roleId}>
                  <span class="text-sm font-medium">{choice.label}</span>
                  <span class="text-xs text-muted-foreground">{choice.description}</span>
                </RadioGroup.Card>
              {/each}
            </RadioGroup.Root>
          </div>
        {:else}
        <!-- Role multi-select -->
        <div class="space-y-2">
          <Label>Roles</Label>
          <div class="grid gap-2 @sm:grid-cols-2">
            {#each grantableRoles as role (role.id)}
              <div class="flex items-center gap-2">
                <Checkbox
                  id="invite-role-{role.id}"
                  checked={inviteRoleIds.includes(role.id ?? '')}
                  onCheckedChange={() => toggleInviteRole(role.id ?? '')}
                />
                <label
                  for="invite-role-{role.id}"
                  class="text-sm text-foreground cursor-pointer select-none"
                >
                  {role.name}
                </label>
              </div>
            {/each}
          </div>
        </div>
        {/if}

        <div class="space-y-2">
          <Label for="invite-expiry">Link expires</Label>
          <Select
            type="single"
            value={String(expiresInDays)}
            onValueChange={(value) => (expiresInDays = Number(value))}
          >
            <SelectTrigger id="invite-expiry" class="w-full">
              {expiryLabel}
            </SelectTrigger>
            <SelectContent>
              {#each EXPIRY_OPTIONS as option (option.value)}
                <SelectItem value={String(option.value)}>{option.label}</SelectItem>
              {/each}
            </SelectContent>
          </Select>
          <p class="text-xs text-muted-foreground">
            After this the link stops working. Anyone who already joined keeps their access.
          </p>
        </div>

        {#if !grantableChoices.length}
        <!-- Direct permissions (collapsible) -->
        <Collapsible.Root
          open={showInvitePermissions}
          onOpenChange={(open: boolean) => (showInvitePermissions = open)}
        >
          <Collapsible.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <Button {...props} variant="subtle" size="inline" class="w-full justify-start">
                {#if showInvitePermissions}
                  <ChevronUp class="h-4 w-4" />
                {:else}
                  <ChevronDown class="h-4 w-4" />
                {/if}
                Direct permissions (optional)
              </Button>
            {/snippet}
          </Collapsible.Trigger>
          <Collapsible.Content>
            <div class="mt-3">
              <PermissionCategorySelector bind:selected={inviteDirectPermissions} />
            </div>
          </Collapsible.Content>
        </Collapsible.Root>
        {/if}

        {#if offerHistoryLimit}
        <div
          class="flex items-start gap-2 rounded-md border p-3 bg-muted/30"
        >
          <Checkbox
            id="limit-to-24-hours"
            checked={limitTo24Hours}
            onCheckedChange={(checked: boolean) => {
              limitTo24Hours = checked === true;
            }}
          />
          <div class="flex-1">
            <label
              for="limit-to-24-hours"
              class="text-sm font-medium cursor-pointer select-none"
            >
              Only last 24 hours
            </label>
            <p class="text-xs text-muted-foreground mt-0.5">
              Restrict access to only the most recent 24 hours of data.
            </p>
          </div>
        </div>
        {/if}

        {#if !singleUseOnly}
        <div
          class="flex items-start gap-2 rounded-md border p-3 bg-muted/30"
        >
          <Checkbox
            id="allow-multiple-uses"
            checked={allowMultipleUses}
            onCheckedChange={(checked: boolean) => {
              allowMultipleUses = checked === true;
            }}
          />
          <div class="flex-1">
            <label
              for="allow-multiple-uses"
              class="text-sm font-medium cursor-pointer select-none"
            >
              Allow multiple uses
            </label>
            <p class="text-xs text-muted-foreground mt-0.5">
              By default, invite links can only be used once.
            </p>
          </div>
        </div>
        {/if}

        <FormError issues={errorMessage} focusOnShow />

        <div class="flex gap-3">
          <Button
            type="button"
            variant="outline"
            class="flex-1"
            onclick={handleCancel}
          >
            Cancel
          </Button>
          <Button
            type="button"
            class="flex-1"
            disabled={isCreatingInvite ||
              (inviteRoleIds.length === 0 &&
                inviteDirectPermissions.length === 0)}
            onclick={handleCreateInvite}
          >
            {#if isCreatingInvite}
              <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
            {/if}
            Create link
          </Button>
        </div>
      </div>
    {/if}
  </Card.Content>
</Card.Root>
