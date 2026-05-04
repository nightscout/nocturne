<script lang="ts">
	import { page } from "$app/state";
	import { goto } from "$app/navigation";
	import * as Card from "$lib/components/ui/card";
	import { Button } from "$lib/components/ui/button";
	import { Input } from "$lib/components/ui/input";
	import { Label } from "$lib/components/ui/label";
	import { Badge } from "$lib/components/ui/badge";
	import { Link2, Link2Off, Plus, Star, Pencil, Save, X, Loader2 } from "lucide-svelte";
	import {
		getLinks,
		setDefault,
		updateLink,
		revokeLink,
	} from "$lib/api/generated/chatIdentities.generated.remote";
	import { getDiscordConfig, initiateDiscordLink } from "./discord.remote";

	// Auth guard
	$effect(() => {
		if (!page.data.isAuthenticated) {
			goto(`/auth/login?returnUrl=${encodeURIComponent(page.url.pathname)}`, {
				replaceState: true,
			});
		}
	});

	// Queries
	const linksQuery = $derived(getLinks());
	const links = $derived(linksQuery.current ?? []);
	const configQuery = $derived(getDiscordConfig());
	const config = $derived(configQuery.current);

	let editingId: string | null = $state(null);
	let editLabel = $state("");
	let editDisplayName = $state("");

	// Action states
	let isLinking = $state(false);
	let actionError = $state<string | null>(null);
	let isSettingDefault = $state<string | null>(null);
	let isSavingEdit = $state(false);
	let isRevoking = $state<string | null>(null);

	function startEdit(id: string, label: string, displayName: string) {
		editingId = id;
		editLabel = label;
		editDisplayName = displayName;
	}

	function cancelEdit() {
		editingId = null;
		editLabel = "";
		editDisplayName = "";
	}

	async function handleSetDefault(id: string) {
		isSettingDefault = id;
		actionError = null;
		try {
			await setDefault(id);
		} catch {
			actionError = "Failed to set default link.";
		} finally {
			isSettingDefault = null;
		}
	}

	async function handleUpdateLink(id: string) {
		isSavingEdit = true;
		actionError = null;
		try {
			await updateLink({
				id,
				request: {
					label: editLabel || undefined,
					displayName: editDisplayName || undefined,
				},
			});
			cancelEdit();
		} catch {
			actionError = "Failed to update link.";
		} finally {
			isSavingEdit = false;
		}
	}

	async function handleRevokeLink(id: string) {
		isRevoking = id;
		actionError = null;
		try {
			await revokeLink(id);
		} catch {
			actionError = "Failed to revoke link.";
		} finally {
			isRevoking = null;
		}
	}

	async function handleLinkDiscord() {
		isLinking = true;
		actionError = null;
		try {
			const result = await initiateDiscordLink(undefined);
			if (result && "error" in result) {
				actionError = result.error;
			} else if (result && "redirectUrl" in result) {
				window.location.href = result.redirectUrl;
				return;
			}
		} catch {
			actionError = "Failed to start Discord link flow.";
		} finally {
			isLinking = false;
		}
	}

	const botInviteUrl = $derived(
		config?.discordApplicationId
			? `https://discord.com/api/oauth2/authorize?client_id=${config.discordApplicationId}&scope=bot+applications.commands&permissions=2147484672`
			: null,
	);
</script>

<div class="container mx-auto max-w-2xl p-6 space-y-6">
	<div>
		<h1 class="text-2xl font-bold">Discord Integration</h1>
		<p class="text-muted-foreground">
			Link your Discord account to query glucose and receive alerts through the Nocturne bot.
		</p>
	</div>

	{#if actionError}
		<Card.Root class="border-destructive">
			<Card.Content class="pt-6">
				<p class="text-sm text-destructive">{actionError}</p>
			</Card.Content>
		</Card.Root>
	{/if}

	<Card.Root>
		<Card.Header>
			<Card.Title>Linked accounts</Card.Title>
			<Card.Description>
				Discord accounts linked to <strong>this Nocturne instance</strong>. Each one can be
				queried from Discord with <code>/bg &lt;label&gt;</code>.
			</Card.Description>
		</Card.Header>
		<Card.Content class="space-y-3">
			{#if links.length === 0}
				<p class="text-sm text-muted-foreground">
					No Discord accounts linked yet. Use the button below to connect one.
				</p>
			{:else}
				{#each links as link (link.id)}
					<div class="flex flex-col gap-2 p-3 border rounded-md">
						{#if editingId === link.id}
							<div class="space-y-2">
								<div class="space-y-1">
									<Label for="label-{link.id}">Label (used in <code>/bg &lt;label&gt;</code>)</Label>
									<Input
										id="label-{link.id}"
										bind:value={editLabel}
										pattern="[a-z0-9][a-z0-9\-]{'{ 0,62}'}[a-z0-9]?"
										placeholder="e.g. lily"
										required
									/>
								</div>
								<div class="space-y-1">
									<Label for="displayName-{link.id}">Display name</Label>
									<Input
										id="displayName-{link.id}"
										bind:value={editDisplayName}
										placeholder="e.g. Lily"
										required
									/>
								</div>
								<div class="flex gap-2">
									<Button size="sm" disabled={isSavingEdit} onclick={() => handleUpdateLink(link.id ?? "")}>
										{#if isSavingEdit}
											<Loader2 class="size-4 mr-1 animate-spin" />
										{:else}
											<Save class="size-4 mr-1" />
										{/if}
										Save
									</Button>
									<Button type="button" variant="ghost" size="sm" onclick={cancelEdit}>
										<X class="size-4 mr-1" />
										Cancel
									</Button>
								</div>
							</div>
						{:else}
							<div class="flex items-center justify-between gap-2">
								<div class="flex items-center gap-2 min-w-0 flex-1">
									<Link2 class="size-4 text-muted-foreground shrink-0" />
									<div class="min-w-0">
										<div class="font-medium flex items-center gap-2">
											<span class="truncate">{link.displayName}</span>
											{#if link.isDefault}
												<Badge variant="secondary" class="shrink-0">Default</Badge>
											{/if}
										</div>
										<div class="text-xs text-muted-foreground truncate">
											<code>{link.label}</code>
											{#if link.platformUserId}
												· Discord <code>{link.platformUserId}</code>
											{/if}
										</div>
									</div>
								</div>
								<div class="flex gap-1 shrink-0">
									{#if !link.isDefault}
										<Button
											size="icon"
											variant="ghost"
											title="Set as default"
											disabled={isSettingDefault === link.id}
											onclick={() => handleSetDefault(link.id ?? "")}
										>
											{#if isSettingDefault === link.id}
												<Loader2 class="size-4 animate-spin" />
											{:else}
												<Star class="size-4" />
											{/if}
										</Button>
									{/if}
									<Button
										type="button"
										size="icon"
										variant="ghost"
										title="Edit"
										onclick={() => startEdit(link.id ?? "", link.label ?? "", link.displayName ?? "")}
									>
										<Pencil class="size-4" />
									</Button>
									<Button
										type="button"
										size="icon"
										variant="ghost"
										title="Revoke"
										disabled={isRevoking === link.id}
										onclick={() => handleRevokeLink(link.id ?? "")}
									>
										{#if isRevoking === link.id}
											<Loader2 class="size-4 animate-spin" />
										{:else}
											<Link2Off class="size-4" />
										{/if}
									</Button>
								</div>
							</div>
						{/if}
					</div>
				{/each}
			{/if}
		</Card.Content>
		<Card.Footer class="flex flex-col gap-2 items-stretch">
			{#if config?.isOauthConfigured}
				<Button class="w-full" disabled={isLinking} onclick={handleLinkDiscord}>
					{#if isLinking}
						<Loader2 class="size-4 mr-2 animate-spin" />
					{:else}
						<Plus class="size-4 mr-2" />
					{/if}
					Link my Discord account
				</Button>
			{:else}
				<p class="text-xs text-muted-foreground">
					Discord OAuth2 is not configured on this instance. Ask the administrator to set
					<code>DISCORD_APPLICATION_ID</code> and <code>DISCORD_CLIENT_SECRET</code>, or run
					<code>/connect</code> directly from Discord.
				</p>
			{/if}
		</Card.Footer>
	</Card.Root>

	{#if botInviteUrl}
		<Card.Root>
			<Card.Header>
				<Card.Title>Add the bot to a Discord server</Card.Title>
				<Card.Description>
					Invite the Nocturne bot to a Discord server you manage so you can use
					<code>/bg</code> there.
				</Card.Description>
			</Card.Header>
			<Card.Content>
				<Button href={botInviteUrl} target="_blank" rel="noopener noreferrer" variant="outline">
					Open Discord invite
				</Button>
			</Card.Content>
		</Card.Root>
	{/if}
</div>
