<script lang="ts">
  import { SvelteSet } from "svelte/reactivity";
  import { Label } from "$lib/components/ui/label";
  import { Input } from "$lib/components/ui/input";
  import { Switch } from "$lib/components/ui/switch";
  import { Button } from "$lib/components/ui/button";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "$lib/components/ui/select";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Separator } from "$lib/components/ui/separator";
  import { Badge } from "$lib/components/ui/badge";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import {
    Eye,
    EyeOff,
    Loader2,
    Save,
    RotateCcw,
    ChevronDown,
    ChevronRight,
    Lock,
  } from "lucide-svelte";
  import type {
    JsonSchema,
    JsonSchemaProperty,
  } from "$lib/utils/connector-json-schema";
  import { getPropertyMeta } from "$lib/config/connectorPropertyMeta";

  interface Props {
    schema: JsonSchema;
    configuration: Record<string, unknown>;
    secrets?: Record<string, string>;
    /** Effective configuration from the running connector (env var values) */
    effectiveConfig?: Record<string, unknown> | null;
    /** Whether secrets are configured (from API) */
    hasSecrets?: boolean;
    onSave: (config: Record<string, unknown>, secrets: Record<string, string>) => Promise<void>;
  }

  let {
    schema,
    configuration = $bindable(),
    secrets = $bindable({}),
    effectiveConfig = null,
    hasSecrets = false,
    onSave,
  }: Props = $props();

  // Track which secret fields are visible
  let visibleSecrets = $state(new SvelteSet<string>());

  // Track initial configuration for unsaved change detection
  let initialConfiguration = $state<Record<string, unknown>>({});
  let hasInitialized = $state(false);

  // Local saving state
  let isSaving = $state(false);

  // Initialize initial config when configuration changes (on load/save)
  $effect(() => {
    if (!hasInitialized && Object.keys(configuration).length > 0) {
      initialConfiguration = { ...configuration };
      hasInitialized = true;
    }
  });

  const hasAnyUnsavedChanges = $derived.by(() => {
    const allKeys = new Set([
      ...Object.keys(configuration),
      ...Object.keys(initialConfiguration),
    ]);
    for (const key of allKeys) {
      if (String(configuration[key] ?? "") !== String(initialConfiguration[key] ?? "")) {
        return true;
      }
    }
    for (const value of Object.values(secrets)) {
      if (value && value.trim()) return true;
    }
    return false;
  });

  // Track if advanced section is expanded
  let advancedExpanded = $state(false);

  // Category priority order
  const CATEGORY_ORDER: Record<string, number> = {
    General: 0,
    Connection: 1,
    Credentials: 2, // Rendered in credentials card, not here
    Sync: 3,
    Advanced: 100, // Always last
    Other: 99,
  };

  // Group properties by category
  const groupedProperties = $derived.by(() => {
    const groups: Record<
      string,
      {
        name: string;
        order: number;
        properties: [string, JsonSchemaProperty][];
      }
    > = {};
    const secretFieldSet = new Set(schema.secrets ?? []);

    // Initialize category groups
    for (const propName of Object.keys(schema.properties)) {
      const propSchema = schema.properties[propName];

      // Skip secret fields - they're handled separately
      if (secretFieldSet.has(propName)) continue;

      // Skip 'enabled' field - it's controlled by the "Enable Connector" toggle
      if (propName.toLowerCase() === "enabled") continue;

      const category = getPropertyMeta(propName).category;

      // Skip credential-category fields - they're shown in the credentials card
      if (category === "Credentials") continue;

      if (!groups[category]) {
        groups[category] = {
          name: category,
          order: CATEGORY_ORDER[category] ?? 50,
          properties: [],
        };
      }

      groups[category].properties.push([propName, propSchema]);
    }

    // Filter empty groups and sort
    return Object.values(groups)
      .filter((g) => g.properties.length > 0)
      .sort((a, b) => a.order - b.order);
  });

  // Separate main and advanced groups
  const mainGroups = $derived(
    groupedProperties.filter((g) => g.name !== "Advanced")
  );
  const advancedGroup = $derived(
    groupedProperties.find((g) => g.name === "Advanced")
  );

  // Get secret fields
  const secretFields = $derived.by(() => {
    const secretNames = schema.secrets ?? [];
    return secretNames
      .map((name) => ({
        name,
        schema: schema.properties[name],
      }))
      .filter((s) => s.schema);
  });

  // Get non-secret fields in the Credentials category
  const credentialFields = $derived.by(() => {
    const secretFieldSet = new Set(schema.secrets ?? []);
    return Object.entries(schema.properties)
      .filter(
        ([name]) =>
          getPropertyMeta(name).category === "Credentials" && !secretFieldSet.has(name)
      )
      .map(([name, schema]) => ({ name, schema }));
  });

  function getPropertyValue(propName: string): unknown {
    // Priority: user configuration > effective config from connector > schema default
    if (propName in configuration && configuration[propName] !== undefined) {
      return configuration[propName];
    }
    if (effectiveConfig && propName in effectiveConfig) {
      return effectiveConfig[propName];
    }
    return schema.properties[propName]?.default;
  }

  function getEnvVarValue(propName: string): unknown {
    // Get the value from the running connector (env var source)
    if (effectiveConfig && propName in effectiveConfig) {
      return effectiveConfig[propName];
    }
    return undefined;
  }

  function hasUnsavedChanges(propName: string): boolean {
    // Check if the current value differs from the initial loaded value
    const currentValue = configuration[propName];
    const initialValue = initialConfiguration[propName];

    if (currentValue === undefined && initialValue === undefined) return false;
    return String(currentValue ?? "") !== String(initialValue ?? "");
  }

  function hasEnvOverride(propName: string): boolean {
    // Check if there's an env var value that differs from the schema default
    const envValue = getEnvVarValue(propName);
    if (envValue === undefined) return false;

    const defaultValue = schema.properties[propName]?.default;
    return String(envValue) !== String(defaultValue ?? "");
  }

  function hasUserOverride(propName: string): boolean {
    // Check if user has set a value different from the env var
    if (!(propName in configuration) || configuration[propName] === undefined) {
      return false;
    }
    const envValue = getEnvVarValue(propName);
    if (envValue === undefined) {
      return false;
    }
    // Compare values (handle type coercion for booleans/numbers)
    return String(configuration[propName]) !== String(envValue);
  }

  function resetToEnvVar(propName: string) {
    const envValue = getEnvVarValue(propName);
    if (envValue !== undefined) {
      configuration = { ...configuration, [propName]: envValue };
    } else {
      // Remove user override
      const { [propName]: _, ...rest } = configuration;
      configuration = rest;
    }
  }

  function setPropertyValue(propName: string, value: unknown) {
    configuration = { ...configuration, [propName]: value };
  }

  function getSecretValue(propName: string): string {
    return secrets[propName] ?? "";
  }

  function setSecretValue(propName: string, value: string) {
    secrets = { ...secrets, [propName]: value };
  }

  function toggleSecretVisibility(propName: string) {
    if (visibleSecrets.has(propName)) {
      visibleSecrets.delete(propName);
    } else {
      visibleSecrets.add(propName);
    }
  }

  async function handleSave() {
    isSaving = true;
    try {
      const nonEmptySecrets: Record<string, string> = {};
      for (const [key, value] of Object.entries(secrets)) {
        if (value && value.trim()) nonEmptySecrets[key] = value;
      }
      await onSave(configuration, nonEmptySecrets);
      // onSave resolves after loadConnectorData runs in the parent,
      // so configuration now has the latest backend values
      initialConfiguration = { ...configuration };
      secrets = {};
    } finally {
      isSaving = false;
    }
  }

  function handleCancel() {
    configuration = { ...initialConfiguration };
    secrets = {};
    visibleSecrets.clear();
  }

</script>

{#snippet propertyField(propName: string, propSchema: JsonSchemaProperty)}
  {@const meta = getPropertyMeta(propName)}
  <div class="space-y-2">
    {#if propSchema.type === "boolean"}
      <!-- Boolean: Switch -->
      <div class="flex items-center justify-between">
        <div class="space-y-0.5">
          <div class="flex items-center gap-2">
            <Label>{meta.label}</Label>
            {#if hasUnsavedChanges(propName)}
              <Badge variant="default" class="text-xs">Unsaved</Badge>
            {:else if hasUserOverride(propName)}
              <Badge variant="secondary" class="text-xs">Modified</Badge>
              <Button
                variant="ghost"
                size="sm"
                class="h-5 px-1"
                title="Reset to environment variable value"
                onclick={() => resetToEnvVar(propName)}
              >
                <RotateCcw class="h-3 w-3" />
              </Button>
            {:else if hasEnvOverride(propName)}
              <Badge variant="outline" class="text-xs">From Env</Badge>
            {/if}
          </div>
          {#if meta.description}
            <p class="text-sm text-muted-foreground">
              {meta.description}
            </p>
          {/if}
          {#if propSchema["x-envVar"]}
            <p class="text-xs text-muted-foreground/70">
              <code class="bg-muted px-1 rounded">
                {propSchema["x-envVar"]}
              </code>
            </p>
          {/if}
        </div>
        <Switch
          checked={Boolean(getPropertyValue(propName))}
          onCheckedChange={(checked) => setPropertyValue(propName, checked)}
        />
      </div>
    {:else if propSchema.enum}
      <!-- Enum: Select -->
      <div class="flex items-center gap-2">
        <Label>{meta.label}</Label>
        {#if hasUnsavedChanges(propName)}
          <Badge variant="default" class="text-xs">Unsaved</Badge>
        {:else if hasUserOverride(propName)}
          <Badge variant="secondary" class="text-xs">Modified</Badge>
          <Button
            variant="ghost"
            size="sm"
            class="h-5 px-1"
            title="Reset to environment variable value"
            onclick={() => resetToEnvVar(propName)}
          >
            <RotateCcw class="h-3 w-3" />
          </Button>
        {:else if hasEnvOverride(propName)}
          <Badge variant="outline" class="text-xs">From Env</Badge>
        {/if}
      </div>
      <Select
        type="single"
        value={String(getPropertyValue(propName) ?? propSchema.default ?? "")}
        onValueChange={(value) => setPropertyValue(propName, value)}
      >
        <SelectTrigger>
          <span>{getPropertyValue(propName) ?? "Select..."}</span>
        </SelectTrigger>
        <SelectContent>
          {#each propSchema.enum as option (option)}
            <SelectItem value={option}>{option}</SelectItem>
          {/each}
        </SelectContent>
      </Select>
      {#if meta.description}
        <p class="text-sm text-muted-foreground">{meta.description}</p>
      {/if}
      {#if propSchema["x-envVar"]}
        <p class="text-xs text-muted-foreground/70">
          <code class="bg-muted px-1 rounded">{propSchema["x-envVar"]}</code>
        </p>
      {/if}
    {:else if propSchema.type === "integer" || propSchema.type === "number"}
      <!-- Number: Input with constraints -->
      <div class="flex items-center gap-2">
        <Label>{meta.label}</Label>
        {#if hasUnsavedChanges(propName)}
          <Badge variant="default" class="text-xs">Unsaved</Badge>
        {:else if hasUserOverride(propName)}
          <Badge variant="secondary" class="text-xs">Modified</Badge>
          <Button
            variant="ghost"
            size="sm"
            class="h-5 px-1"
            title="Reset to environment variable value"
            onclick={() => resetToEnvVar(propName)}
          >
            <RotateCcw class="h-3 w-3" />
          </Button>
        {:else if hasEnvOverride(propName)}
          <Badge variant="outline" class="text-xs">From Env</Badge>
        {/if}
      </div>
      <Input
        type="number"
        value={String(getPropertyValue(propName) ?? "")}
        min={propSchema.minimum}
        max={propSchema.maximum}
        oninput={(e) => {
          const target = e.currentTarget;
          const value =
            propSchema.type === "integer"
              ? parseInt(target.value)
              : parseFloat(target.value);
          if (!isNaN(value)) {
            setPropertyValue(propName, value);
          }
        }}
      />
      {#if meta.description}
        <p class="text-sm text-muted-foreground">{meta.description}</p>
      {/if}
      {#if propSchema.minimum !== undefined || propSchema.maximum !== undefined}
        <p class="text-xs text-muted-foreground">
          {#if propSchema.minimum !== undefined && propSchema.maximum !== undefined}
            Value must be between {propSchema.minimum} and {propSchema.maximum}
          {:else if propSchema.minimum !== undefined}
            Minimum: {propSchema.minimum}
          {:else if propSchema.maximum !== undefined}
            Maximum: {propSchema.maximum}
          {/if}
        </p>
      {/if}
      {#if propSchema["x-envVar"]}
        <p class="text-xs text-muted-foreground/70">
          <code class="bg-muted px-1 rounded">{propSchema["x-envVar"]}</code>
        </p>
      {/if}
    {:else}
      <!-- String: Input -->
      <div class="flex items-center gap-2">
        <Label>{meta.label}</Label>
        {#if hasUnsavedChanges(propName)}
          <Badge variant="default" class="text-xs">Unsaved</Badge>
        {:else if hasUserOverride(propName)}
          <Badge variant="secondary" class="text-xs">Modified</Badge>
          <Button
            variant="ghost"
            size="sm"
            class="h-5 px-1"
            title="Reset to environment variable value"
            onclick={() => resetToEnvVar(propName)}
          >
            <RotateCcw class="h-3 w-3" />
          </Button>
        {:else if hasEnvOverride(propName)}
          <Badge variant="outline" class="text-xs">From Env</Badge>
        {/if}
      </div>
      <Input
        type={propSchema.format === "uri" ? "url" : "text"}
        value={String(getPropertyValue(propName) ?? "")}
        placeholder={propSchema.format === "uri" ? "https://..." : undefined}
        minlength={propSchema.minLength}
        maxlength={propSchema.maxLength}
        pattern={propSchema.pattern}
        oninput={(e) => setPropertyValue(propName, e.currentTarget.value)}
      />
      {#if meta.description}
        <p class="text-sm text-muted-foreground">{meta.description}</p>
      {/if}
      {#if propSchema["x-envVar"]}
        <p class="text-xs text-muted-foreground/70">
          <code class="bg-muted px-1 rounded">{propSchema["x-envVar"]}</code>
        </p>
      {/if}
    {/if}
  </div>
{/snippet}

<div class="space-y-6">
  <!-- Main Configuration Fields by Category -->
  {#each mainGroups as group (group.name)}
    <Card>
      <CardHeader>
        <CardTitle>{group.name}</CardTitle>
      </CardHeader>
      <CardContent class="space-y-4">
        {#each group.properties as [propName, propSchema], i (propName)}
          {@render propertyField(propName, propSchema)}
          {#if i < group.properties.length - 1}
            <Separator />
          {/if}
        {/each}
      </CardContent>
    </Card>
  {/each}

  <!-- Advanced Settings (Collapsible) -->
  {#if advancedGroup && advancedGroup.properties.length > 0}
    <Collapsible.Root bind:open={advancedExpanded}>
      <Card>
        <Collapsible.Trigger class="w-full">
          <CardHeader
            class="cursor-pointer hover:bg-muted/50 transition-colors"
          >
            <div class="flex items-center justify-between">
              <CardTitle class="flex items-center gap-2">
                {advancedGroup.name}
                <Badge variant="secondary" class="text-xs font-normal">
                  {advancedGroup.properties.length} settings
                </Badge>
              </CardTitle>
              {#if advancedExpanded}
                <ChevronDown class="h-4 w-4 text-muted-foreground" />
              {:else}
                <ChevronRight class="h-4 w-4 text-muted-foreground" />
              {/if}
            </div>
          </CardHeader>
        </Collapsible.Trigger>
        <Collapsible.Content>
          <CardContent class="space-y-4 pt-0">
            {#each advancedGroup.properties as [propName, propSchema], i (propName)}
              {@render propertyField(propName, propSchema)}
              {#if i < advancedGroup.properties.length - 1}
                <Separator />
              {/if}
            {/each}
          </CardContent>
        </Collapsible.Content>
      </Card>
    </Collapsible.Root>
  {/if}

  <!-- Credentials Section -->
  {#if secretFields.length > 0 || credentialFields.length > 0}
    <Separator class="my-6" />

    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Lock class="h-4 w-4" />
          Credentials
        </CardTitle>
        <CardDescription>
          Sensitive credentials are stored encrypted and never displayed after
          saving.
          {#if hasSecrets}
            <Badge variant="outline" class="ml-2 text-xs">Configured</Badge>
          {:else}
            <Badge variant="destructive" class="ml-2 text-xs">
              Not configured
            </Badge>
          {/if}
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        {#each credentialFields as { name, schema: propSchema }, i (name)}
          {@render propertyField(name, propSchema)}
          {#if i < credentialFields.length - 1 || secretFields.length > 0}
            <Separator />
          {/if}
        {/each}
        {#each secretFields as { name, schema: propSchema }, i (name)}
          {@const secretMeta = getPropertyMeta(name)}
          <div class="space-y-2">
            <Label>{secretMeta.label}</Label>
            <div class="flex gap-2">
              <Input
                type={visibleSecrets.has(name) ? "text" : "password"}
                value={getSecretValue(name)}
                placeholder="Enter to update (leave blank to keep current)"
                oninput={(e) => setSecretValue(name, e.currentTarget.value)}
                class="flex-1"
              />
              <Button
                variant="outline"
                size="icon"
                onclick={() => toggleSecretVisibility(name)}
              >
                {#if visibleSecrets.has(name)}
                  <EyeOff class="h-4 w-4" />
                {:else}
                  <Eye class="h-4 w-4" />
                {/if}
              </Button>
            </div>
            {#if secretMeta.description}
              <p class="text-sm text-muted-foreground">
                {secretMeta.description}
              </p>
            {/if}
            {#if propSchema["x-envVar"]}
              <p class="text-xs text-muted-foreground/70">
                Environment variable: <code class="bg-muted px-1 rounded">
                  {propSchema["x-envVar"]}
                </code>
              </p>
            {/if}
          </div>
          {#if i < secretFields.length - 1}
            <Separator />
          {/if}
        {/each}
      </CardContent>
    </Card>

  {/if}

  <!-- Sticky Save Bar -->
  {#if hasAnyUnsavedChanges}
    <div class="sticky bottom-0 -mx-6 border-t bg-background px-6 py-4 flex items-center justify-between gap-4">
      <p class="text-sm text-muted-foreground">You have unsaved changes</p>
      <div class="flex gap-2">
        <Button variant="outline" onclick={handleCancel} disabled={isSaving}>
          Cancel
        </Button>
        <Button onclick={handleSave} disabled={isSaving}>
          {#if isSaving}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Saving...
          {:else}
            <Save class="mr-2 h-4 w-4" />
            Save
          {/if}
        </Button>
      </div>
    </div>
  {/if}
</div>
