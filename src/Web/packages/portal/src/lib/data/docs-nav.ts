import type { Pathname } from "$app/types";
import type { DOCS_SECTION_IDS } from "$lib/analytics";

export type DocsSectionId = (typeof DOCS_SECTION_IDS)[number];

export type DocsNavItem = { href: Pathname; label: string };

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
        title: "Getting started",
        items: [
            { href: "/docs", label: "Overview" },
            { href: "/docs/getting-started", label: "Quick start" },
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
            { href: "/docs/installation/byo-postgres", label: "Bring your own PostgreSQL" },
            { href: "/docs/installation/reverse-proxy", label: "Bring your own reverse proxy" },
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
        title: "Sharing & privacy",
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
        title: "Food & carbs",
        items: [
            { href: "/docs/food", label: "Overview" },
            { href: "/docs/food/carbs", label: "Carb entries" },
            { href: "/docs/food/catalog", label: "The food catalog" },
            { href: "/docs/food/meals", label: "Meals & attribution" },
            { href: "/docs/food/deduplication", label: "Duplicate carbs" },
        ],
    },
    {
        id: "trackers",
        title: "Trackers",
        items: [
            { href: "/docs/trackers", label: "Overview" },
            { href: "/docs/trackers/setting-up", label: "Setting up a tracker" },
            { href: "/docs/trackers/everyday-use", label: "Everyday use" },
        ],
    },
    {
        id: "reports",
        title: "Reports",
        items: [
            { href: "/docs/reports", label: "Overview" },
            { href: "/docs/reports/executive-summary", label: "Executive summary" },
            { href: "/docs/reports/agp", label: "Glucose profile (AGP)" },
            { href: "/docs/reports/glucose-distribution", label: "Glucose distribution" },
            { href: "/docs/reports/hourly-stats", label: "Hourly patterns" },
            { href: "/docs/reports/readings", label: "Readings" },
            { href: "/docs/reports/day-in-review", label: "Day in review" },
            { href: "/docs/reports/week-to-week", label: "Week to week" },
            { href: "/docs/reports/comparison", label: "Comparison" },
            { href: "/docs/reports/year-overview", label: "Year overview" },
            { href: "/docs/reports/ehba1c", label: "Estimated HbA1c" },
            { href: "/docs/reports/treatments", label: "Treatment log" },
            { href: "/docs/reports/insulin-delivery", label: "Insulin delivery" },
            { href: "/docs/reports/basal-analysis", label: "Basal analysis" },
            { href: "/docs/reports/idp", label: "Insulin dosing profile" },
            { href: "/docs/reports/site-change-impact", label: "Site change impact" },
            { href: "/docs/reports/loop-analysis", label: "Loop analysis" },
            { href: "/docs/reports/loop-analysis/daily-profile", label: "Daily profile" },
            { href: "/docs/reports/loop-analysis/meals-and-events", label: "Meals and events" },
            { href: "/docs/reports/loop-analysis/prediction-accuracy", label: "Prediction accuracy" },
            { href: "/docs/reports/loop-analysis/loop-health", label: "Loop health" },
            { href: "/docs/reports/loop-analysis/settings-evidence", label: "Settings evidence" },
            { href: "/docs/reports/data-quality", label: "Data quality" },
            { href: "/docs/reports/battery", label: "Battery" },
            { href: "/docs/reports/steps", label: "Step count" },
            { href: "/docs/reports/heart-rate", label: "Heart rate" },
            { href: "/docs/reports/sleep", label: "Sleep & overnight" },
        ],
    },
    {
        id: "alerts",
        title: "Alerts",
        items: [
            { href: "/docs/alerts", label: "Overview" },
            { href: "/docs/alerts/building-rules", label: "Building a rule" },
            { href: "/docs/alerts/conditions", label: "Conditions" },
            { href: "/docs/alerts/combining", label: "All of, any of, NOT" },
            { href: "/docs/alerts/sustained", label: "Conditions that must last" },
            { href: "/docs/alerts/when-an-alert-fires", label: "When an alert fires" },
            { href: "/docs/alerts/recipes", label: "Example rules" },
            { href: "/docs/alerts/testing", label: "Testing rules" },
            { href: "/docs/alerts/email", label: "Email (Resend)" },
        ],
    },
    {
        id: "bots",
        title: "Chat bots",
        items: [
            { href: "/docs/bots", label: "Overview" },
            { href: "/docs/bots/discord", label: "Discord" },
            { href: "/docs/bots/slack", label: "Slack" },
            { href: "/docs/bots/telegram", label: "Telegram" },
            { href: "/docs/bots/whatsapp", label: "WhatsApp" },
        ],
    },
    {
        id: "data-sources",
        title: "Data sources",
        items: [{ href: "/docs/data-sources/google-health", label: "Google Health" }],
    },
    {
        id: "configuration",
        title: "Configuration",
        items: [{ href: "/docs/configuration", label: "Configuration guide" }],
    },
    {
        id: "observability",
        title: "Observability",
        items: [{ href: "/docs/observability", label: "OpenTelemetry" }],
    },
    {
        id: "windows-widget",
        title: "Windows widget",
        items: [{ href: "/docs/windows-widget", label: "Overview & setup" }],
    },
    {
        id: "connecting-apps",
        title: "Connecting apps",
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
        title: "API reference",
        items: [{ href: "/scalar", label: "Interactive API docs" }],
    },
];
