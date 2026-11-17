import { getContext, onMount, setContext } from "svelte";

// @wc-ignore
const CONTEXT = "nocturne:portal:install-guide";

// Storage keys, variable names and byte counts, not user-facing text.
// @wc-ignore
const DOMAIN_KEY = "nocturne-docs:install-domain";
// @wc-ignore
const CHOICES_KEY = "nocturne-docs:install-choices:";

// WATCHTOWER_API_TOKEN is filled only where the template lists it.
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
export function isDomain(value: string): boolean {
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

/** A variable is filled in place, also where the template comments it out as optional. */
export function fillEnvTemplate(template: string, values: Record<string, string>): string {
    return template
        .split("\n")
        .map((line) => {
            const match = /^#?\s*([A-Z0-9_]+)=/.exec(line);
            return match && match[1] in values ? `${match[1]}=${values[match[1]]}` : line;
        })
        .join("\n");
}

function readStorage(key: string): string | null {
    try {
        return localStorage.getItem(key);
    } catch {
        // Storage is blocked. The guide works without it for this page load.
        return null;
    }
}

function writeStorage(key: string, value: string): void {
    try {
        localStorage.setItem(key, value);
    } catch {
        // See readStorage.
    }
}

export interface InstallGuideOptions {
    /** Names the guide's stored choices. */
    id: string;
    /** The release's .env.example, for a guide that makes the keys file. */
    envTemplate?: string;
}

/**
 * What the reader told one installation guide: the domain, the route taken at each choice, and
 * the keys file made for them. The domain is kept in the browser's storage and shared by every
 * guide. The keys never are.
 */
export class InstallGuide {
    readonly id: string;
    readonly #envTemplate: string | undefined;

    domainInput = $state("");
    /** The domain field in the sticky bar, so a step can send the reader to it. */
    domainField = $state<HTMLInputElement | null>(null);

    // The portal is prerendered, so values made during the build would be the same for every
    // visitor. They are made in onMount, which runs only in the browser.
    secrets = $state<Record<string, string> | null>(null);
    fileName = $state("nocturne-keys.env");
    downloadedFile = $state<string | null>(null);
    saved = $state(false);

    #choices = $state<Record<string, string>>({});

    readonly domain = $derived(this.domainInput.trim());
    readonly domainValid = $derived(isDomain(this.domain));
    /** The domain once it is usable in an address or a command. */
    readonly host = $derived(this.domainValid ? this.domain : null);

    readonly values: Record<string, string> | null = $derived(
        this.secrets ? { ...this.secrets, BASE_DOMAIN: this.domain } : null,
    );
    readonly envFile = $derived.by(() => this.#fill());
    /** The download matches the file as it is now. A change after the download needs a new one. */
    readonly downloadCurrent = $derived(
        this.envFile !== null && this.downloadedFile === this.envFile,
    );
    /** The reader holds a copy of the keys, so the page may hand the values out. */
    readonly keysReady = $derived(this.downloadCurrent && this.saved);

    constructor(options: InstallGuideOptions) {
        this.id = options.id;
        this.#envTemplate = options.envTemplate;
    }

    #fill(): string | null {
        return this.values && this.#envTemplate !== undefined
            ? fillEnvTemplate(this.#envTemplate, this.values)
            : null;
    }

    choice(id: string, fallback: string): string {
        return this.#choices[id] ?? fallback;
    }

    choose(id: string, value: string): void {
        this.#choices = { ...this.#choices, [id]: value };
        writeStorage(CHOICES_KEY + this.id, JSON.stringify(this.#choices));
    }

    setDomain(value: string): void {
        this.domainInput = value;
        if (value.trim() === "" || isDomain(value.trim())) writeStorage(DOMAIN_KEY, value.trim());
    }

    focusDomain(): void {
        this.domainField?.focus();
    }

    download(): void {
        if (!this.envFile || !this.domainValid) return;
        const url = URL.createObjectURL(new Blob([this.envFile], { type: "text/plain" }));
        const link = document.createElement("a");
        link.href = url;
        link.download = this.fileName;
        link.click();
        URL.revokeObjectURL(url);
        this.downloadedFile = this.envFile;
        this.saved = false;
    }

    /** Browser only. */
    start(): void {
        this.domainInput = readStorage(DOMAIN_KEY) ?? "";
        const stored = readStorage(CHOICES_KEY + this.id);
        if (stored) {
            try {
                const parsed: unknown = JSON.parse(stored);
                if (parsed && typeof parsed === "object") {
                    this.#choices = Object.fromEntries(
                        Object.entries(parsed).filter(
                            (entry): entry is [string, string] => typeof entry[1] === "string",
                        ),
                    );
                }
            } catch (error) {
                if (!(error instanceof SyntaxError)) throw error;
            }
        }
        if (this.#envTemplate === undefined) return;
        this.secrets = Object.fromEntries(
            Object.entries(SECRETS).map(([name, bytes]) => [name, hex(bytes)]),
        );
        // en-CA writes a date as year-month-day.
        const today = new Intl.DateTimeFormat("en-CA").format(Date.now());
        this.fileName = `nocturne-keys-${today}.env`;
    }
}

/** Call once, from the guide's page component. */
export function createInstallGuide(options: InstallGuideOptions): InstallGuide {
    const guide = new InstallGuide(options);
    setContext(CONTEXT, guide);
    onMount(() => guide.start());
    return guide;
}

/** The guide of the page this component is on, if that page is an installation guide. */
export function getInstallGuide(): InstallGuide | undefined {
    return getContext<InstallGuide | undefined>(CONTEXT);
}
