import type { DOCS_SECTION_IDS } from "$lib/analytics";

export type DocsSectionId = (typeof DOCS_SECTION_IDS)[number];

export type DocsNavItem = { href: string; label: string };

export type DocsNavSection = {
    /** Stable id reported as the `section` property of a `Docs Nav` event. */
    id: DocsSectionId;
    title: string;
    items: DocsNavItem[];
};

/**
 * The docs sidebar, as data. Icons live in DocsSidebar.svelte rather than here so that this
 * module stays free of Svelte imports and can be unit-tested under plain node.
 */
export const DOCS_NAV_SECTIONS: DocsNavSection[] = [
    {
        id: "getting-started",
        title: "Getting Started",
        items: [
            { href: "/docs", label: "Overview" },
            { href: "/docs/getting-started", label: "Quick Start" },
        ],
    },
    {
        id: "installation",
        title: "Installation",
        items: [
            { href: "/docs/installation", label: "Overview" },
            { href: "/docs/installation/docker-compose", label: "Docker Compose" },
            { href: "/docs/installation/portainer", label: "Portainer" },
            { href: "/docs/installation/oracle-cloud", label: "Oracle Cloud" },
            { href: "/docs/installation/byo-postgres", label: "Bring Your Own PostgreSQL" },
            { href: "/docs/installation/reverse-proxy", label: "Bring Your Own Reverse Proxy" },
        ],
    },
    {
        id: "authentication",
        title: "Authentication",
        items: [
            { href: "/docs/authentication", label: "Overview" },
            { href: "/docs/authentication/passkeys", label: "Passkeys & fallbacks" },
            { href: "/docs/authentication/request-membership", label: "Request membership" },
            { href: "/docs/authentication/google", label: "Sign in with Google" },
            { href: "/docs/authentication/github", label: "Sign in with GitHub" },
            { href: "/docs/authentication/oidc", label: "Generic OIDC" },
        ],
    },
    {
        id: "sharing",
        title: "Sharing & Privacy",
        items: [
            { href: "/docs/sharing", label: "Overview" },
            { href: "/docs/sharing/public-link", label: "Public share link" },
            { href: "/docs/sharing/members", label: "Member accounts & invites" },
            { href: "/docs/sharing/guest-links", label: "Temporary guest links" },
            { href: "/docs/sharing/clock", label: "Clocks" },
        ],
    },
    {
        id: "food",
        title: "Food & Carbs",
        items: [
            { href: "/docs/food", label: "Overview" },
            { href: "/docs/food/carbs", label: "Carb entries" },
            { href: "/docs/food/catalog", label: "The food catalog" },
            { href: "/docs/food/meals", label: "Meals & attribution" },
            { href: "/docs/food/deduplication", label: "Duplicate carbs" },
        ],
    },
    {
        id: "alerts",
        title: "Alerts",
        items: [{ href: "/docs/alerts/email", label: "Email (Resend)" }],
    },
    {
        id: "bots",
        title: "Chat Bots",
        items: [
            { href: "/docs/bots", label: "Overview" },
            { href: "/docs/bots/discord", label: "Discord" },
            { href: "/docs/bots/slack", label: "Slack" },
            { href: "/docs/bots/telegram", label: "Telegram" },
            { href: "/docs/bots/whatsapp", label: "WhatsApp" },
        ],
    },
    {
        id: "configuration",
        title: "Configuration",
        items: [{ href: "/docs/configuration", label: "Configuration Guide" }],
    },
    {
        id: "observability",
        title: "Observability",
        items: [{ href: "/docs/observability", label: "OpenTelemetry" }],
    },
    {
        id: "windows-widget",
        title: "Windows Widget",
        items: [{ href: "/docs/windows-widget", label: "Overview & setup" }],
    },
    {
        id: "connecting-apps",
        title: "Connecting Apps",
        items: [
            { href: "/docs/connecting-apps", label: "App authorization (PKCE)" },
            { href: "/docs/connecting-apps/device-flow", label: "Mobile & device flow" },
        ],
    },
    {
        id: "sdks",
        title: "SDKs",
        items: [{ href: "/docs/sdks", label: "Official SDKs" }],
    },
    {
        id: "api-reference",
        title: "API Reference",
        items: [{ href: "/scalar", label: "Interactive API Docs" }],
    },
];
