<script lang="ts">
    import { onMount } from "svelte";
    import Copy from "@lucide/svelte/icons/copy";
    import Check from "@lucide/svelte/icons/check";
    import Download from "@lucide/svelte/icons/download";
    import { Button } from "@nocturne/ui/ui/button";
    import { Checkbox } from "@nocturne/ui/ui/checkbox";
    import { Input } from "@nocturne/ui/ui/input";
    import { Label } from "@nocturne/ui/ui/label";
    import { copyToClipboard } from "@nocturne/ui/utils";
    import Callout from "@nocturne/cms/components/Callout.svelte";
    import { track } from "#lib/analytics.js";

    interface Props {
        /** The release's .env.example. */
        template: string;
    }

    let { template }: Props = $props();

    // Variable names and byte counts, not user-facing text. WATCHTOWER_API_TOKEN is filled only
    // where the template lists it.
    // @wc-ignore
    const SECRETS: Record<string, number> = {
        INSTANCE_KEY: 32,
        POSTGRES_PASSWORD: 24,
        POSTGRES_MIGRATOR_PASSWORD: 24,
        POSTGRES_APP_PASSWORD: 24,
        POSTGRES_WEB_PASSWORD: 24,
        WATCHTOWER_API_TOKEN: 24,
    };

    // @wc-ignore
    const LABEL_CHARS = /^[a-z0-9-]+$/i;

    function isLabel(label: string): boolean {
        return (
            label.length >= 1 &&
            label.length <= 63 &&
            LABEL_CHARS.test(label) &&
            !label.startsWith("-") &&
            !label.endsWith("-")
        );
    }

    /** A host name of at least two labels, with no scheme, port or path, and not an IP address. */
    function isDomain(value: string): boolean {
        const labels = value.split(".");
        return (
            value.length <= 253 &&
            labels.length >= 2 &&
            labels.every(isLabel) &&
            /^[a-z]/i.test(labels[labels.length - 1])
        );
    }

    function hex(bytes: number): string {
        const buffer = new Uint8Array(bytes);
        crypto.getRandomValues(buffer);
        return Array.from(buffer, (b) => b.toString(16).padStart(2, "0")).join("");
    }

    // The portal is prerendered, so values made during the build would be the same for every
    // visitor. onMount runs only in the browser.
    let secrets = $state<Record<string, string> | null>(null);
    let fileName = $state("nocturne-keys.env");
    onMount(() => {
        secrets = Object.fromEntries(
            Object.entries(SECRETS).map(([name, bytes]) => [name, hex(bytes)])
        );
        const today = new Date();
        const month = String(today.getMonth() + 1).padStart(2, "0");
        const day = String(today.getDate()).padStart(2, "0");
        fileName = `nocturne-keys-${today.getFullYear()}-${month}-${day}.env`;
    });

    let baseDomain = $state("");
    let downloaded = $state<string | null>(null);
    let saved = $state(false);
    let copyState = $state<"idle" | "copied" | "failed">("idle");

    const domain = $derived(baseDomain.trim());
    const domainValid = $derived(isDomain(domain));

    // A variable is filled in place, also where the template comments it out as optional.
    const envFile = $derived.by(() => {
        if (!secrets) return null;
        const values: Record<string, string> = { ...secrets, BASE_DOMAIN: domain };
        return template
            .split("\n")
            .map((line) => {
                const match = /^#?\s*([A-Z0-9_]+)=/.exec(line);
                return match && match[1] in values ? `${match[1]}=${values[match[1]]}` : line;
            })
            .join("\n");
    });

    // Copy is for the file that was downloaded. A change after the download needs a new one.
    const downloadCurrent = $derived(envFile !== null && downloaded === envFile);

    function download() {
        if (!envFile || !domainValid) return;
        const url = URL.createObjectURL(new Blob([envFile], { type: "text/plain" }));
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        link.click();
        URL.revokeObjectURL(url);
        downloaded = envFile;
    }

    async function copy() {
        if (!envFile || !downloadCurrent || !saved) return;
        if (!(await copyToClipboard(envFile))) {
            copyState = "failed";
            return;
        }
        copyState = "copied";
        track("Docs Copy", { kind: "password" });
        setTimeout(() => (copyState = "idle"), 2000);
    }
</script>

<div class="not-prose mb-8 space-y-4">
    <Callout type="warning" title="Keep a copy of this file">
        <p>
            WARNING: Keep <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">INSTANCE_KEY</code>.
            Nocturne uses it to encrypt the passwords and secrets for your alert channels,
            webhooks and connectors. If you lose it, alerts stop at those channels and connectors
            stop until you enter each secret again.
        </p>
        <p>
            This page makes new values each time it loads. Do not make a new file for an
            installation that already runs. The database keeps the passwords from its first start.
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
            aria-invalid={domain !== "" && !domainValid}
            aria-describedby="env-base-domain-hint"
        />
        <p id="env-base-domain-hint" class="text-sm text-muted-foreground">
            {#if domain !== "" && !domainValid}
                Enter only the domain name, for example example.com, without https:// or a path.
            {:else}
                Tenants use subdomains of this domain, for example family.example.com.
            {/if}
        </p>
    </div>

    <div class="rounded-lg border border-border/60 bg-muted/50">
        <pre class="max-h-96 overflow-auto p-3 text-sm"><code
                >{envFile ?? "Your browser makes the values when the page loads."}</code
            ></pre>
    </div>

    <Button variant="outline" onclick={download} disabled={!envFile || !domainValid}>
        <Download class="size-4" />
        Download {fileName}
    </Button>

    <div class="flex items-start gap-2">
        <Checkbox id="env-saved" bind:checked={saved} disabled={!downloadCurrent} class="mt-0.5" />
        <Label for="env-saved" variant="option">
            <span class="leading-snug">
                I saved a copy of this file in a location that is not this server, for example
                a password manager.
            </span>
        </Label>
    </div>

    <div class="space-y-2">
        <Button onclick={copy} disabled={!downloadCurrent || !saved}>
            {#if copyState === "copied"}
                <Check class="size-4" />
                Copied
            {:else}
                <Copy class="size-4" />
                Copy the file
            {/if}
        </Button>
        {#if copyState === "failed"}
            <p class="text-sm text-destructive">
                Could not copy the file. Select the text above and copy it manually.
            </p>
        {:else if envFile && domainValid && !downloadCurrent}
            <p class="text-sm text-muted-foreground">Download the file first. Then you can copy it.</p>
        {/if}
    </div>
</div>
