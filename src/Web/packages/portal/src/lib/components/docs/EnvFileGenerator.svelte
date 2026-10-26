<script lang="ts">
    import Copy from "@lucide/svelte/icons/copy";
    import Check from "@lucide/svelte/icons/check";
    import Download from "@lucide/svelte/icons/download";
    import { Button } from "@nocturne/ui/ui/button";
    import { Checkbox } from "@nocturne/ui/ui/checkbox";
    import { Input } from "@nocturne/ui/ui/input";
    import { Label } from "@nocturne/ui/ui/label";
    import { copyToClipboard } from "@nocturne/ui/utils";
    import Callout from "@nocturne/cms/components/Callout.svelte";

    interface Props {
        /** The release's .env.example. */
        template: string;
    }

    let { template }: Props = $props();

    // Variable names and byte counts, not user-facing text.
    // @wc-ignore
    const SECRETS: Record<string, number> = {
        INSTANCE_KEY: 32,
        POSTGRES_PASSWORD: 24,
        POSTGRES_MIGRATOR_PASSWORD: 24,
        POSTGRES_APP_PASSWORD: 24,
        POSTGRES_WEB_PASSWORD: 24,
        WATCHTOWER_API_TOKEN: 24,
    };

    function hex(bytes: number): string {
        const buffer = new Uint8Array(bytes);
        crypto.getRandomValues(buffer);
        return Array.from(buffer, (b) => b.toString(16).padStart(2, "0")).join("");
    }

    // The portal is prerendered, so values made during the build would be the same for every
    // visitor. Effects run only in the browser.
    let secrets = $state<Record<string, string> | null>(null);
    let fileName = $state("nocturne-keys.env");
    $effect(() => {
        if (secrets) return;
        secrets = Object.fromEntries(
            Object.entries(SECRETS).map(([name, bytes]) => [name, hex(bytes)])
        );
        const today = new Date();
        const month = String(today.getMonth() + 1).padStart(2, "0");
        const day = String(today.getDate()).padStart(2, "0");
        fileName = `nocturne-keys-${today.getFullYear()}-${month}-${day}.env`;
    });

    let baseDomain = $state("");
    let saved = $state(false);
    let copied = $state(false);

    // A variable is filled in place, also where the template comments it out as optional.
    const envFile = $derived.by(() => {
        if (!secrets) return null;
        const values: Record<string, string> = { ...secrets, BASE_DOMAIN: baseDomain.trim() };
        return template
            .split("\n")
            .map((line) => {
                const match = /^#?\s*([A-Z0-9_]+)=/.exec(line);
                return match && match[1] in values ? `${match[1]}=${values[match[1]]}` : line;
            })
            .join("\n");
    });

    function download() {
        if (!envFile) return;
        const url = URL.createObjectURL(new Blob([envFile], { type: "text/plain" }));
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        link.click();
        URL.revokeObjectURL(url);
    }

    async function copy() {
        if (!envFile || !saved) return;
        copied = await copyToClipboard(envFile);
        if (copied) setTimeout(() => (copied = false), 2000);
    }
</script>

<div class="not-prose mb-8 space-y-4">
    <Callout type="warning" title="Keep a copy of this file">
        <p>
            WARNING: Keep <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">INSTANCE_KEY</code>.
            Nocturne uses it to encrypt the connector passwords and other secrets that it stores.
            If you lose it, Nocturne cannot read them, and you must enter them again.
        </p>
        <p>
            Do not make a new file for an installation that already runs. The database keeps the
            passwords from its first start.
        </p>
    </Callout>

    <div class="space-y-2">
        <Label for="env-base-domain">Your domain</Label>
        <Input
            id="env-base-domain"
            bind:value={baseDomain}
            placeholder="example.com"
            autocomplete="off"
            spellcheck={false}
        />
    </div>

    <div class="rounded-lg border border-border/60 bg-muted/50">
        <pre class="max-h-96 overflow-auto p-3 text-sm"><code
                >{envFile ?? "Your browser makes the values when the page loads."}</code
            ></pre>
    </div>

    <Button variant="outline" onclick={download} disabled={!envFile}>
        <Download class="size-4" />
        Download {fileName}
    </Button>

    <div class="flex items-start gap-2">
        <Checkbox id="env-saved" bind:checked={saved} class="mt-0.5" />
        <Label for="env-saved" variant="option">
            <span class="leading-snug">
                I saved a copy of this file in a location that is not this server, for example
                a password manager.
            </span>
        </Label>
    </div>

    <Button onclick={copy} disabled={!envFile || !saved}>
        {#if copied}
            <Check class="size-4" />
            Copied
        {:else}
            <Copy class="size-4" />
            Copy the file
        {/if}
    </Button>
</div>
