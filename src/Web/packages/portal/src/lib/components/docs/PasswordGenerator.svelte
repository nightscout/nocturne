<script lang="ts">
    import { Copy, Check, RefreshCw } from "@lucide/svelte";

    interface Props {
        label?: string;
        length?: number;
    }

    let { label = "password", length = 32 }: Props = $props();

    const ALPHABET =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#%^&*-_=+";

    function generatePassword(len: number): string {
        const bytes = new Uint8Array(len * 2);
        crypto.getRandomValues(bytes);
        let result = "";
        let i = 0;
        while (result.length < len) {
            const byte = bytes[i % bytes.length];
            i++;
            // Rejection sampling to avoid modulo bias
            const limit = 256 - (256 % ALPHABET.length);
            if (byte < limit) {
                result += ALPHABET[byte % ALPHABET.length];
            }
            if (i >= bytes.length) {
                // Refill if rejection used too many bytes
                crypto.getRandomValues(bytes);
                i = 0;
            }
        }
        return result;
    }

    let refreshKey = $state(0);
    // Derived so password regenerates whenever length or refreshKey changes.
    // The generatePassword call reads neither reactive state nor props directly —
    // refreshKey and length are the only tracked dependencies here.
    let password = $derived.by(() => {
        // Track both dependencies explicitly.
        const _key = refreshKey;
        return generatePassword(length);
    });
    let copied = $state(false);
    let copyTimer: ReturnType<typeof setTimeout> | undefined;

    function refresh() {
        refreshKey += 1;
        copied = false;
        if (copyTimer !== undefined) {
            clearTimeout(copyTimer);
            copyTimer = undefined;
        }
    }

    async function copy() {
        await navigator.clipboard.writeText(password);
        copied = true;
        if (copyTimer !== undefined) {
            clearTimeout(copyTimer);
        }
        copyTimer = setTimeout(() => {
            copied = false;
            copyTimer = undefined;
        }, 2000);
    }
</script>

<div class="not-prose my-4">
    <div
        class="flex items-center gap-2 rounded-lg border border-border/60 bg-muted/50 px-4 py-3"
    >
        <code
            class="flex-1 font-mono text-sm break-all select-all"
            aria-label="Generated {label}"
        >
            {password}
        </code>

        <div class="flex items-center gap-1 shrink-0">
            <button
                type="button"
                onclick={refresh}
                class="rounded-md p-1.5 text-muted-foreground transition-colors hover:bg-background hover:text-foreground"
                aria-label="Generate new {label}"
            >
                <RefreshCw class="h-4 w-4" />
            </button>

            <button
                type="button"
                onclick={copy}
                class="rounded-md p-1.5 text-muted-foreground transition-colors hover:bg-background hover:text-foreground"
                aria-label={copied ? "Copied" : `Copy ${label} to clipboard`}
            >
                {#if copied}
                    <Check class="h-4 w-4 text-green-500" />
                {:else}
                    <Copy class="h-4 w-4" />
                {/if}
            </button>
        </div>
    </div>

    <p class="mt-1.5 text-xs text-muted-foreground">
        Generated locally in your browser — never sent anywhere.
    </p>
</div>
