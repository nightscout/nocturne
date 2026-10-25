import { guide } from "./guide.svelte";
import type { HelpArticle } from "./documentation-types";

export function pageArticles(): HelpArticle[] {
  return [
    guide(
      "/",
      "Dashboard",
      "See recent glucose and the surrounding recorded events.",
      "Readings, treatments, device status, and profiles from your uploaders and connectors.",
      "Use chart controls on this page. Settings → Appearance controls units and display. Therapy shows personal schedules. Connectors & Apps manages incoming data. Alerts manages notifications.",
      "The same stored records feed the calendar and reports.",
      "A latest value can become stale if uploads stop. Gaps and missing treatments limit context. The server classifies the current glucose status. Report bands, personal targets, and alerts serve different purposes.",
      ["/reports", "/settings/connectors", "/settings/appearance", "/alerts"]
    ),
    guide(
      "/calendar",
      "Calendar",
      "Explore recorded events and glucose by day.",
      "Stored readings and events with their timestamps.",
      "Choose dates and the available view controls here. Display format is in Appearance. Event records are in Treatments. Timezone history is under Patient Record.",
      "The calendar helps you find a day to inspect on the timeline or in reports.",
      "Only recorded events appear. Local dates and timezone changes affect which day an event belongs to.",
      ["/reports/day-in-review", "/reports/treatments", "/settings/timezone"]
    ),
    guide(
      "/time-spans",
      "Time Spans",
      "Inspect periods when a device or recorded state was active.",
      "Imported state spans such as pump modes, connectivity, and basal delivery states.",
      "Use this page's filters and timeline controls. Manage the source connector in Connectors & Apps.",
      "Spans provide duration and state context for device and delivery analysis.",
      "A span is a period, not one reading. Open-ended or missing state records can limit its displayed end and duration.",
      ["/settings/connectors", "/reports/basal-analysis"]
    ),
    guide(
      "/clock",
      "Clock",
      "Choose a clock display for a glance at recent data.",
      "The current tenant's glucose/device data and saved clock configurations.",
      "Create or open a clock configuration here to choose its layout and display options. General units and theme are in Appearance.",
      "Clock faces present the same incoming data in a compact display.",
      "A clock is not a separate data source. Check freshness if uploads stop. Its configured units can differ from your general display preference.",
      ["/settings/appearance", "/settings/connectors"]
    ),
    guide(
      "/food",
      "Food",
      "Manage food entries and their nutrition information.",
      "Food catalog records saved or imported into this tenant.",
      "Use the page's search, add, and edit controls. Review portion size and carb units before saving.",
      "Food information can be used when recording meals and carb intake.",
      "A catalog entry is not a record that you ate it. Portion size and nutrition estimates can be inaccurate. Check the original source.",
      ["/meals", "/reports/treatments"]
    ),
    guide(
      "/meals",
      "Meals",
      "Review meals and their recorded carb context.",
      "Recorded meals, food/portion information, and associated treatment records.",
      "Use the page's date, add, and edit controls. Manage reusable foods in Food and treatment records in Treatments.",
      "Meal timing and carbs provide context beside glucose and insulin records.",
      "Missing food details do not mean no meal. A nearby glucose change does not establish a meal's effect.",
      ["/food", "/reports/treatments", "/reports/day-in-review"]
    ),
    guide(
      "/tools/packing",
      "Packing",
      "Estimate supplies to take on a trip.",
      "Your chosen trip details, supply categories, and available packing hints.",
      "Set trip details and review supply estimates here, then open the packing list. Tracker definitions are in Notifications & Trackers.",
      "The list turns the estimate into items you can check off and customize.",
      "This is an estimate. Check your actual supplies, device needs, delays, and a suitable spare amount before travelling.",
      ["/tools/packing/list", "/settings/trackers"]
    ),
    guide(
      "/tools/packing/list",
      "Packing list",
      "Check off and customize the supplies for your trip.",
      "The trip estimate and custom items you add.",
      "Use the item checkboxes and Custom field here. Return to Packing to change trip inputs.",
      "The checklist helps organize packing. It does not monitor the contents of your bag.",
      "Checking an item does not verify that you physically packed it. Review quantities and changes to the trip.",
      ["/tools/packing"]
    ),
    guide(
      "/alerts",
      "Alert Rules",
      "Choose conditions that trigger a notification.",
      "Saved rules evaluated against the data and conditions they reference.",
      "Create or edit a rule here for its condition, thresholds, timing, and notification actions. Test it in Simulator. Review deliveries in History and quiet periods in Do Not Disturb.",
      "Enabled rules are used by the alert system. They do not change report reference bands.",
      "Delivery depends on fresh data, rule conditions, enabled channels, and quiet settings. A rule threshold is separate from a personal target. Test a change before relying on it.",
      ["/alerts/simulator", "/alerts/history", "/alerts/dnd"]
    ),
    guide(
      "/alerts/simulator",
      "Alert Simulator",
      "Test how an alert rule responds to sample conditions.",
      "The rule and the scenario values chosen for the simulation.",
      "Select the rule and test inputs here. Edit real rules on Alert Rules.",
      "Results help you inspect rule behavior before enabling or changing it.",
      "Simulation is not a live reading and does not guarantee notification delivery. Check History for actual evaluations and deliveries.",
      ["/alerts", "/alerts/history"]
    ),
    guide(
      "/alerts/history",
      "Alert History",
      "Review recorded alert activity.",
      "Alert evaluations and delivery results retained by Nocturne.",
      "Use the history filters and details here. Change rules in Alert Rules and quiet periods in Do Not Disturb.",
      "History helps explain which rule fired and what happened to its notification.",
      "A recorded alert is not proof the recipient saw it. Missing source data and delivery failures need separate investigation.",
      ["/alerts", "/alerts/dnd"]
    ),
    guide(
      "/alerts/dnd",
      "Do Not Disturb",
      "Manage quiet periods for notifications.",
      "The saved quiet schedules and active Do Not Disturb configuration.",
      "Set the applicable quiet times here. Review each rule's behavior on Alert Rules.",
      "Quiet settings affect notification handling, not glucose imports or report calculations.",
      "Check timezone and the rules/channels affected by your choice. Quiet mode does not stop the underlying measurements.",
      ["/alerts", "/alerts/history"]
    ),
    guide(
      "/compatibility",
      "Compatibility",
      "Inspect Nightscout endpoint compatibility.",
      "Recorded compatibility test results and endpoint metadata.",
      "Open a result or run a test under Test Endpoint Compatibility.",
      "Results help developers compare Nocturne with a reference implementation.",
      "A passing endpoint test only covers its tested scenario. It does not verify every uploader or all historical data.",
      ["/compatibility/test"]
    ),
    guide(
      "/compatibility/test",
      "Test Endpoint Compatibility",
      "Compare an endpoint with a configured reference.",
      "Responses and timings returned by the selected test targets.",
      "Choose endpoint and parameters here, then inspect the returned comparison. Keep target credentials private.",
      "Saved results appear in the compatibility views.",
      "Results depend on the target's data, availability, and permissions. Matching one response does not prove full compatibility.",
      ["/compatibility"]
    ),
    guide(
      "/tenants",
      "Tenants",
      "Choose between the patient workspaces you can access.",
      "Your memberships and each workspace's permitted summary information.",
      "Select a tenant here or with the tenant selector. Manage sharing inside that tenant. Platform administrators use Tenant Management.",
      "The selected tenant determines the records and settings shown throughout the app.",
      "Access to one tenant does not grant access to another. Verify the selected workspace before editing patient records or connections.",
      ["/settings/members", "/settings/admin/tenants"]
    ),
    guide(
      "/setup",
      "Setup",
      "Follow the guided checklist for a usable Nocturne account and workspace.",
      "Your account, patient configuration, and connected data sources.",
      "Follow the checklist here. Individual settings remain available afterwards in the normal settings pages.",
      "Setup connects the basic configuration to the dashboard and reports.",
      "Completing a checklist does not manufacture readings. Verify an actual source import and the selected dates.",
      ["/settings/patient", "/settings/connectors", "/settings/appearance"]
    ),
  ];
}
