<script lang="ts">
	import { Label } from '$lib/components/ui/label';
	import { Input } from '$lib/components/ui/input';
	import { Switch } from '$lib/components/ui/switch';
	import { Button } from '$lib/components/ui/button';
	import {
		Select,
		SelectContent,
		SelectItem,
		SelectTrigger,
	} from '$lib/components/ui/select';
	import {
		Card,
		CardContent,
		CardDescription,
		CardHeader,
		CardTitle,
	} from '$lib/components/ui/card';
	import { Separator } from '$lib/components/ui/separator';
	import { Eye, EyeOff, Loader2, Save } from 'lucide-svelte';
	import type { JsonSchema, JsonSchemaProperty } from '$lib/data/connectorConfig.remote';

	interface Props {
		schema: JsonSchema;
		configuration: Record<string, unknown>;
		secrets?: Record<string, string>;
		onSaveConfiguration: (config: Record<string, unknown>) => Promise<void>;
		onSaveSecrets?: (secrets: Record<string, string>) => Promise<void>;
		isSaving?: boolean;
	}

	let {
		schema,
		configuration = $bindable(),
		secrets = $bindable({}),
		onSaveConfiguration,
		onSaveSecrets,
		isSaving = false,
	}: Props = $props();

	// Track which secret fields are visible
	let visibleSecrets = $state<Set<string>>(new Set());

	// Group properties by category
	const groupedProperties = $derived(() => {
		const groups: Record<string, { name: string; order: number; properties: [string, JsonSchemaProperty][] }> = {};
		const categories = schema.categories ?? {};
		const secretFields = new Set(schema.secrets ?? []);

		// Create category groups
		for (const [category, propertyNames] of Object.entries(categories)) {
			if (!groups[category]) {
				groups[category] = {
					name: category,
					order: getCategoryOrder(category),
					properties: [],
				};
			}
		}

		// Add uncategorized group for any remaining properties
		if (!groups['Other']) {
			groups['Other'] = { name: 'Other', order: 999, properties: [] };
		}

		// Sort properties into groups
		for (const [propName, propSchema] of Object.entries(schema.properties)) {
			// Skip secret fields - they're handled separately
			if (secretFields.has(propName)) continue;

			let added = false;
			for (const [category, propertyNames] of Object.entries(categories)) {
				if (propertyNames.includes(propName)) {
					groups[category].properties.push([propName, propSchema]);
					added = true;
					break;
				}
			}
			if (!added) {
				groups['Other'].properties.push([propName, propSchema]);
			}
		}

		// Filter empty groups and sort
		return Object.values(groups)
			.filter((g) => g.properties.length > 0)
			.sort((a, b) => a.order - b.order);
	});

	// Get secret fields
	const secretFields = $derived(() => {
		const secretNames = schema.secrets ?? [];
		return secretNames.map((name) => ({
			name,
			schema: schema.properties[name],
		})).filter(s => s.schema);
	});

	function getCategoryOrder(category: string): number {
		const order: Record<string, number> = {
			General: 0,
			Connection: 1,
			Sync: 2,
			Advanced: 3,
		};
		return order[category] ?? 50;
	}

	function getPropertyValue(propName: string): unknown {
		return configuration[propName] ?? schema.properties[propName]?.default;
	}

	function setPropertyValue(propName: string, value: unknown) {
		configuration = { ...configuration, [propName]: value };
	}

	function getSecretValue(propName: string): string {
		return secrets[propName] ?? '';
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
		visibleSecrets = new Set(visibleSecrets);
	}

	async function handleSaveConfiguration() {
		await onSaveConfiguration(configuration);
	}

	async function handleSaveSecrets() {
		if (onSaveSecrets) {
			// Only send non-empty secrets
			const nonEmptySecrets: Record<string, string> = {};
			for (const [key, value] of Object.entries(secrets)) {
				if (value && value.trim()) {
					nonEmptySecrets[key] = value;
				}
			}
			if (Object.keys(nonEmptySecrets).length > 0) {
				await onSaveSecrets(nonEmptySecrets);
			}
		}
	}

	function formatLabel(propName: string, propSchema: JsonSchemaProperty): string {
		return propSchema.title ?? formatPropertyName(propName);
	}

	function formatPropertyName(name: string): string {
		// Convert camelCase to Title Case with spaces
		return name
			.replace(/([A-Z])/g, ' $1')
			.replace(/^./, (s) => s.toUpperCase())
			.trim();
	}
</script>

<div class="space-y-6">
	<!-- Configuration Fields by Category -->
	{#each groupedProperties() as group}
		<Card>
			<CardHeader>
				<CardTitle>{group.name}</CardTitle>
			</CardHeader>
			<CardContent class="space-y-4">
				{#each group.properties as [propName, propSchema], i}
					{#if i > 0}
						<Separator />
					{/if}

					<div class="space-y-2">
						{#if propSchema.type === 'boolean'}
							<!-- Boolean: Switch -->
							<div class="flex items-center justify-between">
								<div class="space-y-0.5">
									<Label>{formatLabel(propName, propSchema)}</Label>
									{#if propSchema.description}
										<p class="text-sm text-muted-foreground">{propSchema.description}</p>
									{/if}
								</div>
								<Switch
									checked={Boolean(getPropertyValue(propName))}
									onCheckedChange={(checked) => setPropertyValue(propName, checked)}
								/>
							</div>
						{:else if propSchema.enum}
							<!-- Enum: Select -->
							<Label>{formatLabel(propName, propSchema)}</Label>
							<Select
								type="single"
								value={String(getPropertyValue(propName) ?? propSchema.default ?? '')}
								onValueChange={(value) => setPropertyValue(propName, value)}
							>
								<SelectTrigger>
									<span>{getPropertyValue(propName) ?? 'Select...'}</span>
								</SelectTrigger>
								<SelectContent>
									{#each propSchema.enum as option}
										<SelectItem value={option}>{option}</SelectItem>
									{/each}
								</SelectContent>
							</Select>
							{#if propSchema.description}
								<p class="text-sm text-muted-foreground">{propSchema.description}</p>
							{/if}
						{:else if propSchema.type === 'integer' || propSchema.type === 'number'}
							<!-- Number: Input with constraints -->
							<Label>{formatLabel(propName, propSchema)}</Label>
							<Input
								type="number"
								value={String(getPropertyValue(propName) ?? '')}
								min={propSchema.minimum}
								max={propSchema.maximum}
								oninput={(e) => {
									const target = e.currentTarget;
									const value = propSchema.type === 'integer'
										? parseInt(target.value)
										: parseFloat(target.value);
									if (!isNaN(value)) {
										setPropertyValue(propName, value);
									}
								}}
							/>
							{#if propSchema.description}
								<p class="text-sm text-muted-foreground">{propSchema.description}</p>
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
						{:else}
							<!-- String: Input -->
							<Label>{formatLabel(propName, propSchema)}</Label>
							<Input
								type={propSchema.format === 'uri' ? 'url' : 'text'}
								value={String(getPropertyValue(propName) ?? '')}
								placeholder={propSchema.format === 'uri' ? 'https://...' : undefined}
								minlength={propSchema.minLength}
								maxlength={propSchema.maxLength}
								pattern={propSchema.pattern}
								oninput={(e) => setPropertyValue(propName, e.currentTarget.value)}
							/>
							{#if propSchema.description}
								<p class="text-sm text-muted-foreground">{propSchema.description}</p>
							{/if}
						{/if}
					</div>
				{/each}
			</CardContent>
		</Card>
	{/each}

	<!-- Save Configuration Button -->
	<div class="flex justify-end">
		<Button onclick={handleSaveConfiguration} disabled={isSaving}>
			{#if isSaving}
				<Loader2 class="mr-2 h-4 w-4 animate-spin" />
				Saving...
			{:else}
				<Save class="mr-2 h-4 w-4" />
				Save Configuration
			{/if}
		</Button>
	</div>

	<!-- Secrets Section -->
	{#if secretFields().length > 0}
		<Separator class="my-6" />

		<Card>
			<CardHeader>
				<CardTitle>Credentials</CardTitle>
				<CardDescription>
					Sensitive credentials are stored encrypted and never displayed after saving.
				</CardDescription>
			</CardHeader>
			<CardContent class="space-y-4">
				{#each secretFields() as { name, schema: propSchema }, i}
					{#if i > 0}
						<Separator />
					{/if}

					<div class="space-y-2">
						<Label>{formatLabel(name, propSchema)}</Label>
						<div class="flex gap-2">
							<Input
								type={visibleSecrets.has(name) ? 'text' : 'password'}
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
						{#if propSchema.description}
							<p class="text-sm text-muted-foreground">{propSchema.description}</p>
						{/if}
					</div>
				{/each}
			</CardContent>
		</Card>

		<!-- Save Secrets Button -->
		<div class="flex justify-end">
			<Button onclick={handleSaveSecrets} disabled={isSaving} variant="secondary">
				{#if isSaving}
					<Loader2 class="mr-2 h-4 w-4 animate-spin" />
					Saving...
				{:else}
					<Save class="mr-2 h-4 w-4" />
					Update Credentials
				{/if}
			</Button>
		</div>
	{/if}
</div>
