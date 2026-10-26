<script lang="ts">
  import { enumValue } from "$lib/components/ui/enum-value";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Alert from "$lib/components/ui/alert";
  import * as Select from "$lib/components/ui/select";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Check from "@lucide/svelte/icons/check";
  import Globe from "@lucide/svelte/icons/globe";
  import { OidcProviderType } from "$api";
  import type { OidcProviderResponse, OidcProviderTestResult, TenantRoleDto } from "$api";
  import { describeSubmitError } from "$lib/forms/submit-error";

  const OIDC_SCOPES = "openid profile email";

  // Apple has one issuer and names its own scopes; the Apple logo on a black button is what its
  // sign-in button guidelines ask for.
  const APPLE_PRESET = {
    issuerUrl: "https://appleid.apple.com",
    scopes: "openid name email",
    icon: "apple",
    buttonColor: "#000000",
  };

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
      } satisfies Record<string, string>,
    },
  };

  interface Props {
    open: boolean;
    editingProvider: OidcProviderResponse | null;
    roles: TenantRoleDto[];
    onSave: (providerData: Record<string, unknown>) => Promise<void>;
    onCancel: () => void;
  }

  let {
    open = $bindable(false),
    editingProvider = $bindable<OidcProviderResponse | null>(null),
    roles = $bindable<TenantRoleDto[]>([]),
    onSave,
    onCancel,
  }: Props = $props();

  // `roles` is accepted for callers but read only through the binding.
  void roles;

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

  // Apple-only: the values the client secret is signed with.
  let appleTeamId = $state("");
  let appleKeyId = $state("");
  let applePrimaryAppId = $state("");
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
    appleTeamId = "";
    appleKeyId = "";
    applePrimaryAppId = "";
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
  const isApple = $derived(providerType === OidcProviderType.Apple);

  const typeLabel = $derived(
    isApple ? "Sign in with Apple" : isOAuth2 ? "OAuth 2.0" : "OpenID Connect",
  );

  // Switching protocol clears the OIDC default scopes when moving to OAuth2 (which has no universal
  // default) and restores them when moving back. Only while creating, so editing keeps customizations.
  function onProviderTypeChange(value: OidcProviderType) {
    const wasApple = providerType === OidcProviderType.Apple;
    providerType = value;
    if (editingProvider) return;
    if (value === OidcProviderType.Apple) {
      applyApplePreset();
      return;
    }
    if (wasApple) {
      if (providerIssuerUrl === APPLE_PRESET.issuerUrl) providerIssuerUrl = "";
      if (providerScopes === APPLE_PRESET.scopes) providerScopes = OIDC_SCOPES;
      if (providerIcon === APPLE_PRESET.icon) providerIcon = "";
      if (providerButtonColor === APPLE_PRESET.buttonColor) providerButtonColor = "";
      if (providerName === "Apple") providerName = "";
    }
    if (value === OidcProviderType.OAuth2 && providerScopes === OIDC_SCOPES) {
      providerScopes = "";
    } else if (value === OidcProviderType.Oidc && providerScopes.trim() === "") {
      providerScopes = OIDC_SCOPES;
    }
  }

  function applyApplePreset() {
    providerIssuerUrl = APPLE_PRESET.issuerUrl;
    providerScopes = APPLE_PRESET.scopes;
    providerIcon = APPLE_PRESET.icon;
    providerButtonColor ||= APPLE_PRESET.buttonColor;
    if (!providerName) providerName = "Apple";
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
        error: describeSubmitError(err, "Test failed"),
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
        apple: isApple
          ? {
              teamId: appleTeamId.trim(),
              keyId: appleKeyId.trim(),
              primaryAppId: applePrimaryAppId.trim() || undefined,
            }
          : undefined,
      };

      await onSave(providerData);
      open = false;
    } catch (err: unknown) {
      providerDialogError = describeSubmitError(err, "Failed to save provider.");
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
      appleTeamId = editingProvider.apple?.teamId ?? "";
      appleKeyId = editingProvider.apple?.keyId ?? "";
      applePrimaryAppId = editingProvider.apple?.primaryAppId ?? "";
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
        {editingProvider ? "Edit identity provider" : "Add identity provider"}
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
        <Input id="provider-name" bind:value={providerName} placeholder="My provider" />
      </div>

      <div class="space-y-2">
        <Label for="provider-type">Type</Label>
        <Select.Root
          type="single"
          value={providerType}
          onValueChange={(v) => {
            const type = enumValue(OidcProviderType, v);
            if (type) onProviderTypeChange(type);
          }}
        >
          <Select.Trigger id="provider-type">
            {typeLabel}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value={OidcProviderType.Oidc} label="OpenID Connect" />
            <Select.Item value={OidcProviderType.OAuth2} label="OAuth 2.0" />
            <Select.Item value={OidcProviderType.Apple} label="Sign in with Apple" />
          </Select.Content>
        </Select.Root>
      </div>

      {#if isApple}
        <Alert.Root>
          <Globe class="h-4 w-4" />
          <Alert.Description>
            <p>In your Apple Developer account, add these to the Services ID:</p>
            <ul class="list-disc list-inside mt-1">
              <li>Return URL: <code>{`{your-domain}`}/api/auth/oidc/apple/callback</code></li>
              <li>
                Server-to-server notification endpoint:
                <code>{`{your-domain}`}/api/auth/oidc/apple/notifications</code>
              </li>
            </ul>
            <p class="mt-1">Apple accepts only HTTPS domains. It does not accept localhost.</p>
          </Alert.Description>
        </Alert.Root>
      {/if}

      {#if isOAuth2}
        <Alert.Root>
          <Globe class="h-4 w-4" />
          <Alert.Description>
            Register an OAuth app with your provider using the callback URL
            <code>{`{your-domain}`}/api/auth/oidc/callback</code>. Need GitHub?
            <Button variant="link" size="inline" onclick={applyGithubPreset}>Use GitHub preset</Button>.
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
        <Label for="provider-client-id">{isApple ? "Services ID" : "Client ID"}</Label>
        <Input
          id="provider-client-id"
          bind:value={providerClientId}
          placeholder={isApple ? "com.example.signin" : ""}
        />
      </div>

      {#if isApple}
        <div class="space-y-2">
          <Label for="provider-apple-team-id">Team ID</Label>
          <Input id="provider-apple-team-id" bind:value={appleTeamId} placeholder="A1B2C3D4E5" />
        </div>
        <div class="space-y-2">
          <Label for="provider-apple-key-id">Key ID</Label>
          <Input id="provider-apple-key-id" bind:value={appleKeyId} placeholder="F6G7H8I9J0" />
          <p class="text-xs text-muted-foreground">
            The ID of the Sign in with Apple private key.
          </p>
        </div>
        <div class="space-y-2">
          <Label for="provider-apple-app-id">Primary App ID</Label>
          <Input
            id="provider-apple-app-id"
            bind:value={applePrimaryAppId}
            placeholder="com.example.app"
          />
          <p class="text-xs text-muted-foreground">
            The bundle ID of the App ID that has the notification endpoint. Apple can send
            notifications to this ID. If you do not set it, Nocturne can refuse them.
          </p>
        </div>
        <div class="space-y-2">
          <Label for="provider-client-secret">Private key</Label>
          <Textarea
            id="provider-client-secret"
            bind:value={providerClientSecret}
            rows={5}
            placeholder={editingProvider
              ? "Leave blank to keep existing"
              : "-----BEGIN PRIVATE KEY-----"}
          />
          <p class="text-xs text-muted-foreground">
            Paste all of the .p8 file that you downloaded from Apple.
          </p>
        </div>
      {:else}
        <div class="space-y-2">
          <Label for="provider-client-secret">Client secret</Label>
          <Input
            id="provider-client-secret"
            type="password"
            bind:value={providerClientSecret}
            placeholder={editingProvider ? "Leave blank to keep existing" : ""}
          />
        </div>
      {/if}

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
        <Label for="provider-roles">Default roles</Label>
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
        <Label for="provider-color">Button color</Label>
        <Input
          id="provider-color"
          bind:value={providerButtonColor}
          placeholder="#1a73e8"
        />
      </div>

      <div class="space-y-2">
        <Label for="provider-order">Display order</Label>
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
        >
          {#if testingProvider}
            <Loader2 class="h-4 w-4 animate-spin" />
          {:else}
            <Globe class="h-4 w-4" />
          {/if}
          Test connection
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
                    {#each testResult.warnings as warn, i (i)}
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
      <Button onclick={handleSave} disabled={providerSaving}>
        {#if providerSaving}
          <Loader2 class="h-4 w-4 animate-spin" />
        {/if}
        {editingProvider ? "Save changes" : "Create provider"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
