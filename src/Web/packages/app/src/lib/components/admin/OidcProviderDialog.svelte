<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Alert from "$lib/components/ui/alert";
  import * as Select from "$lib/components/ui/select";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import {
    Loader2,
    AlertTriangle,
    Check,
    Globe,
  } from "lucide-svelte";
  import { OidcProviderType } from "$api";
  import type { OidcProviderResponse, OidcProviderTestResult, TenantRoleDto } from "$api";

  const OIDC_SCOPES = "openid profile email";

  // GitHub is just one OAuth2 provider, expressed purely as data: the endpoints it would advertise
  // via discovery if it spoke OIDC, plus how to read identity from its user API. Other OAuth2
  // providers (GitLab, Discord, …) are configured the same way; this is a quick-fill, not a code path.
  const GITHUB_PRESET = {
    issuerUrl: "https://github.com",
    scopes: "read:user user:email",
    icon: "github",
    buttonColor: "#24292e",
    oauth2: {
      authorizationEndpoint: "https://github.com/login/oauth/authorize",
      tokenEndpoint: "https://github.com/login/oauth/access_token",
      userInfoEndpoint: "https://api.github.com/user",
      userInfoEmailEndpoint: "https://api.github.com/user/emails",
      claimMappings: {
        sub: "id",
        preferred_username: "login",
        name: "name",
        email: "email",
        picture: "avatar_url",
      } as Record<string, string>,
    },
  };

  let {
    open = $bindable(false),
    editingProvider = $bindable<OidcProviderResponse | null>(null),
    roles = $bindable<TenantRoleDto[]>([]),
    onSave,
    onCancel,
  } = $props<{
    open: boolean;
    editingProvider: OidcProviderResponse | null;
    roles: TenantRoleDto[];
    onSave: (providerData: any) => Promise<void>;
    onCancel: () => void;
  }>();

  // Form field state
  let providerName = $state("");
  let providerType = $state<OidcProviderType>(OidcProviderType.Oidc);
  let providerIssuerUrl = $state("");
  let providerClientId = $state("");
  let providerClientSecret = $state("");
  let providerScopes = $state("openid profile email");
  let providerDefaultRoles = $state("readable");

  // OAuth2-only endpoint configuration (ignored for OIDC providers).
  let oauth2AuthEndpoint = $state("");
  let oauth2TokenEndpoint = $state("");
  let oauth2UserInfoEndpoint = $state("");
  let oauth2UserInfoEmailEndpoint = $state("");
  let oauth2ClaimMappings = $state<Record<string, string>>({});
  let providerIcon = $state("");
  let providerButtonColor = $state("");
  let providerDisplayOrder = $state(0);
  let providerIsEnabled = $state(true);

  // Test connection state
  let testingProvider = $state(false);
  let testResult = $state<OidcProviderTestResult | null>(null);
  let providerDialogError = $state<string | null>(null);
  let providerSaving = $state(false);

  function resetProviderForm() {
    editingProvider = null;
    providerName = "";
    providerType = OidcProviderType.Oidc;
    providerIssuerUrl = "";
    providerClientId = "";
    providerClientSecret = "";
    providerScopes = OIDC_SCOPES;
    providerDefaultRoles = "readable";
    providerIcon = "";
    providerButtonColor = "";
    providerDisplayOrder = 0;
    providerIsEnabled = true;
    oauth2AuthEndpoint = "";
    oauth2TokenEndpoint = "";
    oauth2UserInfoEndpoint = "";
    oauth2UserInfoEmailEndpoint = "";
    oauth2ClaimMappings = {};
    providerDialogError = null;
    testResult = null;
  }

  function parseList(value: string): string[] {
    return value
      .split(/[,\s]+/)
      .map((s) => s.trim())
      .filter((s) => s.length > 0);
  }

  const isOAuth2 = $derived(providerType === OidcProviderType.OAuth2);

  // Switching protocol clears the OIDC default scopes when moving to OAuth2 (which has no universal
  // default) and restores them when moving back. Only while creating, so editing keeps customizations.
  function onProviderTypeChange(value: OidcProviderType) {
    providerType = value;
    if (editingProvider) return;
    if (value === OidcProviderType.OAuth2 && providerScopes === OIDC_SCOPES) {
      providerScopes = "";
    } else if (value === OidcProviderType.Oidc && providerScopes.trim() === "") {
      providerScopes = OIDC_SCOPES;
    }
  }

  // Quick-fill the OAuth2 fields for GitHub. Pure data — the backend treats this like any OAuth2 provider.
  function applyGithubPreset() {
    providerType = OidcProviderType.OAuth2;
    providerIssuerUrl = GITHUB_PRESET.issuerUrl;
    providerScopes = GITHUB_PRESET.scopes;
    providerIcon = GITHUB_PRESET.icon;
    providerButtonColor ||= GITHUB_PRESET.buttonColor;
    oauth2AuthEndpoint = GITHUB_PRESET.oauth2.authorizationEndpoint;
    oauth2TokenEndpoint = GITHUB_PRESET.oauth2.tokenEndpoint;
    oauth2UserInfoEndpoint = GITHUB_PRESET.oauth2.userInfoEndpoint;
    oauth2UserInfoEmailEndpoint = GITHUB_PRESET.oauth2.userInfoEmailEndpoint;
    oauth2ClaimMappings = { ...GITHUB_PRESET.oauth2.claimMappings };
    if (!providerName) providerName = "GitHub";
  }

  async function testProviderConnection() {
    testingProvider = true;
    testResult = null;
    try {
      const { testUnsaved } = await import("$lib/api/generated/oidcProviderAdmins.generated.remote");
      testResult = await testUnsaved({
        issuerUrl: providerIssuerUrl,
        clientId: providerClientId,
        clientSecret: providerClientSecret || undefined,
      });
    } catch (err: unknown) {
      testResult = {
        success: false,
        error: err instanceof Error ? err.message : "Test failed",
      };
    } finally {
      testingProvider = false;
    }
  }

  async function handleSave() {
    providerSaving = true;
    providerDialogError = null;
    try {
      const scopes = parseList(providerScopes);
      const defaultRoles = parseList(providerDefaultRoles);
      const providerData = {
        name: providerName,
        providerType,
        issuerUrl: providerIssuerUrl,
        clientId: providerClientId,
        clientSecret: providerClientSecret || undefined,
        scopes: scopes.length > 0 ? scopes : undefined,
        defaultRoles: defaultRoles.length > 0 ? defaultRoles : undefined,
        icon: providerIcon || undefined,
        buttonColor: providerButtonColor || undefined,
        displayOrder: providerDisplayOrder,
        isEnabled: providerIsEnabled,
        oAuth2: isOAuth2
          ? {
              authorizationEndpoint: oauth2AuthEndpoint,
              tokenEndpoint: oauth2TokenEndpoint,
              userInfoEndpoint: oauth2UserInfoEndpoint,
              userInfoEmailEndpoint: oauth2UserInfoEmailEndpoint || undefined,
              claimMappings: oauth2ClaimMappings,
            }
          : undefined,
      };

      await onSave(providerData);
      open = false;
    } catch (err: unknown) {
      const message =
        err instanceof Error ? err.message : "Failed to save provider";
      providerDialogError = message.includes("would_lock_out_users")
        ? "This change would lock out all users. Ensure at least one authentication method remains available."
        : message;
    } finally {
      providerSaving = false;
    }
  }

  function handleCancel() {
    onCancel();
    open = false;
  }

  // Watch for changes to editingProvider and load form
  $effect(() => {
    if (open && editingProvider) {
      providerName = editingProvider.name ?? "";
      providerType = editingProvider.providerType ?? OidcProviderType.Oidc;
      providerIssuerUrl = editingProvider.issuerUrl ?? "";
      providerClientId = editingProvider.clientId ?? "";
      providerClientSecret = "";
      providerScopes = (editingProvider.scopes ?? ["openid", "profile", "email"]).join(", ");
      providerDefaultRoles = (editingProvider.defaultRoles ?? ["readable"]).join(", ");
      providerIcon = editingProvider.icon ?? "";
      providerButtonColor = editingProvider.buttonColor ?? "";
      providerDisplayOrder = editingProvider.displayOrder ?? 0;
      providerIsEnabled = editingProvider.isEnabled ?? true;
      oauth2AuthEndpoint = editingProvider.oAuth2?.authorizationEndpoint ?? "";
      oauth2TokenEndpoint = editingProvider.oAuth2?.tokenEndpoint ?? "";
      oauth2UserInfoEndpoint = editingProvider.oAuth2?.userInfoEndpoint ?? "";
      oauth2UserInfoEmailEndpoint = editingProvider.oAuth2?.userInfoEmailEndpoint ?? "";
      oauth2ClaimMappings = editingProvider.oAuth2?.claimMappings ?? {};
      providerDialogError = null;
      testResult = null;
    } else if (open && !editingProvider) {
      resetProviderForm();
    }
  });

  // Handle dialog close
  $effect(() => {
    if (!open) {
      resetProviderForm();
    }
  });
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-2xl max-h-[90vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>
        {editingProvider ? "Edit Identity Provider" : "Add Identity Provider"}
      </Dialog.Title>
      <Dialog.Description>
        Configure an external identity provider (OpenID Connect or OAuth 2.0) for single sign-on.
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-2">
      {#if providerDialogError}
        <Alert.Root variant="destructive">
          <AlertTriangle class="h-4 w-4" />
          <Alert.Description>{providerDialogError}</Alert.Description>
        </Alert.Root>
      {/if}

      <div class="space-y-2">
        <Label for="provider-name">Name</Label>
        <Input id="provider-name" bind:value={providerName} placeholder="My Provider" />
      </div>

      <div class="space-y-2">
        <Label for="provider-type">Type</Label>
        <Select.Root
          type="single"
          value={providerType}
          onValueChange={(v) => onProviderTypeChange(v as OidcProviderType)}
        >
          <Select.Trigger id="provider-type">
            {isOAuth2 ? "OAuth 2.0" : "OpenID Connect"}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value={OidcProviderType.Oidc} label="OpenID Connect" />
            <Select.Item value={OidcProviderType.OAuth2} label="OAuth 2.0" />
          </Select.Content>
        </Select.Root>
      </div>

      {#if isOAuth2}
        <Alert.Root>
          <Globe class="h-4 w-4" />
          <Alert.Description>
            Register an OAuth app with your provider using the callback URL
            <code>{`{your-domain}`}/api/auth/oidc/callback</code>. Need GitHub?
            <button type="button" class="underline" onclick={applyGithubPreset}>Use GitHub preset</button>.
          </Alert.Description>
        </Alert.Root>
      {/if}

      <div class="space-y-2">
        <Label for="provider-issuer">
          {isOAuth2 ? "Issuer (identity namespace)" : "Issuer URL"}
        </Label>
        <Input
          id="provider-issuer"
          type="url"
          bind:value={providerIssuerUrl}
          placeholder="https://accounts.example.com"
        />
      </div>

      {#if isOAuth2}
        <div class="space-y-2">
          <Label for="provider-auth-endpoint">Authorization endpoint</Label>
          <Input id="provider-auth-endpoint" type="url" bind:value={oauth2AuthEndpoint} />
        </div>
        <div class="space-y-2">
          <Label for="provider-token-endpoint">Token endpoint</Label>
          <Input id="provider-token-endpoint" type="url" bind:value={oauth2TokenEndpoint} />
        </div>
        <div class="space-y-2">
          <Label for="provider-userinfo-endpoint">Userinfo endpoint</Label>
          <Input id="provider-userinfo-endpoint" type="url" bind:value={oauth2UserInfoEndpoint} />
        </div>
        <div class="space-y-2">
          <Label for="provider-userinfo-email-endpoint">Userinfo email endpoint (optional)</Label>
          <Input
            id="provider-userinfo-email-endpoint"
            type="url"
            bind:value={oauth2UserInfoEmailEndpoint}
          />
          <p class="text-xs text-muted-foreground">
            For providers that return email separately from the profile (returns an array of
            email/primary/verified).
          </p>
        </div>
      {/if}

      <div class="space-y-2">
        <Label for="provider-client-id">Client ID</Label>
        <Input id="provider-client-id" bind:value={providerClientId} />
      </div>

      <div class="space-y-2">
        <Label for="provider-client-secret">Client Secret</Label>
        <Input
          id="provider-client-secret"
          type="password"
          bind:value={providerClientSecret}
          placeholder={editingProvider ? "Leave blank to keep existing" : ""}
        />
      </div>

      <div class="space-y-2">
        <Label for="provider-scopes">Scopes</Label>
        <Input
          id="provider-scopes"
          bind:value={providerScopes}
          placeholder="openid profile email"
        />
        <p class="text-xs text-muted-foreground">Comma or space separated</p>
      </div>

      <div class="space-y-2">
        <Label for="provider-roles">Default Roles</Label>
        <Input
          id="provider-roles"
          bind:value={providerDefaultRoles}
          placeholder="readable"
        />
        <p class="text-xs text-muted-foreground">
          Comma-separated roles assigned to new users from this provider
        </p>
      </div>

      <div class="space-y-2">
        <Label for="provider-icon">Icon</Label>
        <Input
          id="provider-icon"
          bind:value={providerIcon}
          placeholder="google, apple, microsoft, github, or a URL"
        />
      </div>

      <div class="space-y-2">
        <Label for="provider-color">Button Color</Label>
        <Input
          id="provider-color"
          bind:value={providerButtonColor}
          placeholder="#1a73e8"
        />
      </div>

      <div class="space-y-2">
        <Label for="provider-order">Display Order</Label>
        <Input
          id="provider-order"
          type="number"
          bind:value={providerDisplayOrder}
        />
      </div>

      <div class="flex items-center gap-2">
        <Checkbox id="provider-enabled" bind:checked={providerIsEnabled} />
        <Label for="provider-enabled">Enabled</Label>
      </div>

      {#if !isOAuth2}
      <div class="border-t pt-4 space-y-2">
        <Button
          variant="outline"
          onclick={testProviderConnection}
          disabled={testingProvider || !providerIssuerUrl || !providerClientId}
          class="gap-2"
        >
          {#if testingProvider}
            <Loader2 class="h-4 w-4 animate-spin" />
          {:else}
            <Globe class="h-4 w-4" />
          {/if}
          Test Connection
        </Button>
        {#if testResult}
          {#if testResult.success}
            <Alert.Root>
              <Check class="h-4 w-4" />
              <Alert.Description>
                Connection successful{testResult.responseTime
                  ? ` (${testResult.responseTime})`
                  : ""}.
                {#if testResult.warnings && testResult.warnings.length > 0}
                  <ul class="list-disc list-inside mt-1 text-xs">
                    {#each testResult.warnings as warn}
                      <li>{warn}</li>
                    {/each}
                  </ul>
                {/if}
              </Alert.Description>
            </Alert.Root>
          {:else}
            <Alert.Root variant="destructive">
              <AlertTriangle class="h-4 w-4" />
              <Alert.Description>
                {testResult.error || "Connection test failed"}
              </Alert.Description>
            </Alert.Root>
          {/if}
        {/if}
      </div>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={handleCancel}>
        Cancel
      </Button>
      <Button onclick={handleSave} disabled={providerSaving} class="gap-2">
        {#if providerSaving}
          <Loader2 class="h-4 w-4 animate-spin" />
        {/if}
        {editingProvider ? "Save Changes" : "Create Provider"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
