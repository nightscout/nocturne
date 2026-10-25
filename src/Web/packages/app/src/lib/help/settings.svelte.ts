import { guide } from "./guide.svelte";
import type { HelpArticle } from "./documentation-types";

export function settingsArticles(): HelpArticle[] {
  return [
    guide(
      "/settings",
      "Settings",
      "Choose the settings for your account, display, patient data, or connections.",
      "Saved account, browser, tenant, and server configuration.",
      "Open a settings category from this page or the navigation menu. Some controls require owner or administrator permission.",
      "These choices control the relevant display, import, sharing, or analysis features.",
      "Account preferences and patient settings are different. Check the selected tenant before changing shared patient settings.",
      [
        "/settings/account",
        "/settings/appearance",
        "/settings/profile",
        "/settings/connectors",
      ]
    ),
    guide(
      "/settings/account",
      "Account",
      "Manage your sign-in identity and recovery methods.",
      "Your signed-in account and its registered authentication methods.",
      "Use the account fields, passkey, authenticator, and recovery-code controls here. Active browser sessions are in Settings → Active Access.",
      "Your account is used to sign in and identify your membership across tenants.",
      "Recovery codes and authentication secrets grant account access. Save recovery information privately. It is not the patient's clinical record.",
      ["/settings/access", "/settings/patient"]
    ),
    guide(
      "/settings/patient",
      "Patient Record",
      "Describe the person whose data this tenant contains.",
      "Clinical details, devices, and insulins entered here or imported where supported.",
      "Use Clinical Information, Devices, and Insulins here. Therapy schedules are in Settings → Therapy. Dated weights and locations have their own pages.",
      "Patient information provides context for other views and calculations that need it.",
      "Changing a patient detail does not rewrite device measurements. Use the correct tenant and units. Optional missing details can limit dependent features.",
      ["/settings/profile", "/settings/weight", "/settings/timezone"]
    ),
    guide(
      "/settings/appearance",
      "Appearance",
      "Choose how Nocturne and its documentation look.",
      "Your saved display preferences and browser-local documentation preferences.",
      "Set theme, language, glucose units, dates, and times here. Help visibility, layout, transparency, and blur are here and in help's Display options.",
      "Display settings format charts and values. They do not change the underlying stored glucose units or readings.",
      "Open and close help with the book next to Nocturne, the close button, or Escape. Your original page and inputs stay in place. Navigation drawer and Replace page use the existing menu for help; Right drawer uses a panel; Side by side keeps the page usable on wide screens. Hidden help can always be enabled here. Documentation appearance is saved in this browser.",
      []
    ),
    guide(
      "/settings/profile",
      "Therapy",
      "Inspect imported therapy profiles, personal targets, and schedules.",
      "Profiles uploaded by a device/app or received from a connector, with their effective times.",
      "Choose the default profile here and inspect target, basal, carb-ratio, and sensitivity schedules. Correct the source profile in the originating app/device when this page only offers a read-only schedule.",
      "Schedules provide context for insulin calculations, scheduled basal, and personal target overlays.",
      "A scheduled rate is not proof of actual delivery. Historical profile timing matters. Personal targets do not replace the report reference bands or automatically update alert thresholds.",
      ["/settings/connectors", "/reports/basal-analysis", "/alerts"]
    ),
    guide(
      "/settings/data-quality",
      "Data Quality settings",
      "Configure detection and inspect maintenance tools.",
      "Saved quality settings and analyses of stored readings.",
      "Set typical bedtime/wake time and automatic detection here. Use the links to quality reports and the maintenance card for the specific operation.",
      "Detection creates review suggestions. Maintenance can change records subsequently used by charts and reports.",
      "Inspect the affected period and confirmation before cleanup. A suggestion is not proof a reading is incorrect, and changing detection settings is different from accepting a correction.",
      ["/reports/data-quality", "/reports/data-quality/compression-lows"]
    ),
    guide(
      "/settings/trackers",
      "Notifications & Trackers",
      "Track supplies, devices, and recurring events.",
      "Tracker definitions, presets, start events, completions, and notifications recorded in Nocturne.",
      "Use Active, History, Definitions, and Presets on this page. Start or complete a tracker using its controls. Edit the definition for duration and reminder behavior.",
      "Active trackers provide due/urgency information and reminders. Completed events form the history.",
      "A reminder depends on its definition, start time, and enabled notification delivery. It does not confirm a physical device was changed. Completing and editing a definition are different actions.",
      ["/alerts", "/settings/connectors", "/tools/packing"]
    ),
    guide(
      "/settings/access",
      "Active Access",
      "Review browser sessions signed in to your account.",
      "The server's active account sessions and available device information.",
      "Inspect and revoke sessions here. Manage passkeys and other sign-in methods under Account.",
      "Revoking a session requires that browser to authenticate again.",
      "This list is not the list of data-upload devices or API tokens. Those are under Connectors & Apps. Revoking a browser session does not delete patient records.",
      ["/settings/account", "/settings/connectors"]
    ),
    guide(
      "/settings/connectors",
      "Connectors & Apps",
      "Connectors bring information from device services into Nocturne.",
      "Server connectors, uploaders, authorized apps, client devices, and tokens configured for this tenant.",
      "Find your source under Server Connectors and open its setup/settings. Review connection status and latest sync. Apps, uploaders, and API tokens have separate controls here.",
      "Imported records feed the dashboard, reports, and patient histories. Each connector supports its own data types.",
      "A configured connector may still need sign-in or import selection. Connected does not prove a historical import is complete. Empty data may be a permission, date, or source-sync problem.",
      [
        "/settings/connectors/google-health",
        "/reports/steps",
        "/reports/heart-rate",
      ]
    ),
    guide(
      "/settings/members",
      "Sharing & Privacy",
      "Control who can see or change this tenant's information.",
      "Tenant memberships, roles, invitations, guest links, and public-sharing settings.",
      "Use member permissions, invitations, requests, roles, and guest/public links here. Changes require the appropriate management permission.",
      "Permissions control available pages and operations for members and shared viewers.",
      "A guest link grants the access you configure to anyone who possesses it. Review scope and time limits. Revoking access does not remove copies someone already made.",
      ["/settings/access", "/settings/connectors"]
    ),
    guide(
      "/settings/support",
      "Support & Community",
      "Find support links and deployment information.",
      "Server service status, version information, and details you choose to include in a report.",
      "Use the support and issue controls here. Review optional device information and your message before submitting. Tutorial preferences are also here.",
      "Version and error details help maintainers understand a reproducible problem.",
      "Describe the page, expected result, and steps to reproduce. Keep credentials, tokens, and identifiable health records out of a public issue.",
      ["/settings/connectors"]
    ),
    guide(
      "/settings/translations",
      "Translations",
      "Inspect and contribute translated interface text.",
      "Nocturne's translation catalogs and saved contributions.",
      "Choose a language here. Choose your interface language in Appearance. Open a language to edit entries and submit a contribution.",
      "Approved translations supply interface text, including these help pages.",
      "English is the original help text. Untranslated entries fall back to English. Preserve placeholders when contributing a translation.",
      ["/settings/appearance"]
    ),
    guide(
      "/settings/weight",
      "Weight history",
      "Review and record dated body weights.",
      "Manual entries and supported imports, including Google Health weights.",
      "Add the weight in kilograms and its recorded time here. Imported weight selection is in Google Health connector settings.",
      "These entries form the patient's weight history and provide dated context for features that use weight.",
      "Check units and timestamps before saving. A missing entry does not mean zero weight. Repeated readings can come from different sources.",
      ["/settings/patient", "/settings/connectors/google-health"]
    ),
    guide(
      "/settings/timezone",
      "Timezone history",
      "Record when the patient moved to a different timezone.",
      "Dated timezone changes entered here and timezone information recorded during imports.",
      "Choose the location timezone and local arrival time. Fix already-imported data is a separate operation here. Inspect its affected period first.",
      "Timezone history helps place events and daily patterns at the correct local time.",
      "A timezone is not just a fixed UTC offset: daylight-saving changes matter. A correction can affect existing data interpretation. Ordinary display-format settings are in Appearance.",
      ["/settings/patient", "/settings/appearance"]
    ),
    guide(
      "/settings/migration",
      "Data Migration",
      "Bring historical records from Nightscout into Nocturne.",
      "The Nightscout API or MongoDB source you configure.",
      "Choose the migration source, credentials, and optional date range here. Inspect active progress, skipped-record notes, and history.",
      "Successfully imported records become available to the normal charts and reports.",
      "Keep source credentials private. Completion counts and skipped records matter. A finished job does not mean every source record was accepted. Migration is separate from ongoing connector sync.",
      ["/settings/connectors", "/settings/nightscout-transition"]
    ),
    guide(
      "/settings/nightscout-transition",
      "Nightscout Transition",
      "Inspect the transition status between Nightscout and Nocturne.",
      "Server transition configuration and recorded transition status.",
      "Review the status on this page. Migration history is under Data Migration. Ongoing data sources are under Connectors & Apps.",
      "The status helps the administrator see how the deployment is progressing.",
      "Availability depends on the server's transition configuration. It is not a control for changing glucose values.",
      ["/settings/migration", "/settings/connectors"]
    ),
    guide(
      "/settings/admin/tenants",
      "Tenant Management",
      "Manage patient workspaces and platform access.",
      "Platform tenant records and administrator identities.",
      "Platform administrators manage tenant display names, slugs, and active status here.",
      "Tenant selection separates each patient's records, configuration, and membership.",
      "Check the tenant before changing its status. A slug affects its address. Platform permissions differ from a role inside one tenant.",
      ["/tenants"]
    ),
    guide(
      "/settings/admin/connector-cursors",
      "Reset Connector Cursors",
      "Ask a connector to read a chosen historical period again.",
      "Stored connector import watermarks and reset-job progress for the selected tenant.",
      "Platform administrators choose Tenant, connector, and optional Lower bound here, then monitor or cancel the job.",
      "A successful re-read fills or updates the normal imported histories, using normal duplicate protection.",
      "This does not delete source data. It may be useful when an already-completed month received data late. Refresh inventory only scans availability and is not a historical re-import.",
      ["/settings/connectors/google-health", "/settings/admin/tenants"]
    ),
    guide(
      "/settings/integrations/discord",
      "Discord Integration",
      "Link a Discord identity and manage its use with Nocturne.",
      "Your linked chat identities and server bot configuration.",
      "Link, label, select the default, or revoke an identity here. Follow the bot's server-install controls where available.",
      "The linked identity supports bot interactions authorized for your account.",
      "Bot availability depends on deployment configuration. Revoking a link does not delete patient data or messages already sent.",
      ["/settings/account"]
    ),
    guide(
      "/settings/access-requests",
      "Access Requests",
      "Review people requesting access to the instance.",
      "Pending access requests stored by the platform.",
      "Administrators review and approve or deny requests here. Tenant membership is managed in Sharing & Privacy.",
      "Decisions determine whether the requester can obtain the applicable access.",
      "Inspect the identity and requested access. Platform access and membership in an individual tenant are different.",
      ["/settings/members"]
    ),
    guide(
      "/settings/audit",
      "Audit history",
      "Inspect recorded administrative actions.",
      "Audit records retained by this server.",
      "Use the filters and detail controls here. Access requires the relevant permission.",
      "The history helps you understand who changed configuration or access.",
      "Only events recorded by the server appear. Retention and visibility depend on deployment and permissions.",
      []
    ),
  ];
}
