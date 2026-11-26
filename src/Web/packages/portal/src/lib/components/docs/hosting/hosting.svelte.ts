// Storage key and option values, not user-facing text.
// @wc-ignore
const STORAGE_KEY = "nocturne-docs:hosting";

export type Hosting = "managed" | "self-host";

function isHosting(value: unknown): value is Hosting {
    return value === "managed" || value === "self-host";
}

/**
 * How the reader runs Nocturne: on a managed service, or on their own server. Shared by every
 * docs page and kept in the browser's storage, so a choice made on the overview also applies on
 * the quick start.
 *
 * The portal is prerendered, so the choice is unknown during the build and for a reader without
 * JavaScript. Until the reader chooses, every page shows both paths.
 */
export class HostingChoice {
    current = $state<Hosting | null>(null);
    /** The page runs in a browser and has read the stored choice. */
    started = $state(false);

    /** Whether content for `hosting` is shown: always, until the reader chooses. */
    shows(hosting: Hosting | undefined): boolean {
        return hosting === undefined || this.current === null || this.current === hosting;
    }

    choose(hosting: Hosting): void {
        this.current = hosting;
        try {
            localStorage.setItem(STORAGE_KEY, hosting);
        } catch {
            // Storage is blocked. The choice holds for this page load.
        }
    }

    /** Browser only. */
    start(): void {
        if (this.started) return;
        try {
            const stored = localStorage.getItem(STORAGE_KEY);
            if (isHosting(stored)) this.current = stored;
        } catch {
            // See choose.
        }
        this.started = true;
    }
}

export const hosting = new HostingChoice();
