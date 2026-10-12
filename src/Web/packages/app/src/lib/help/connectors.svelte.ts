import { guide } from "./guide.svelte";
import type { HelpArticle } from "./documentation-types";

export function connectorArticles(): HelpArticle[] {
  const google = guide(
    "/settings/connectors/google-health",
    "Google Health",
    "Connect your own Google account, inspect what is available, then choose what Nocturne should import. Follow the steps in order.",
    "Your device or source app sends data to Google Health. Nocturne reads the selected account through Google's API and stores supported records in this tenant's existing histories. A Google Cloud project provides permission to use the API; it does not generate health data.",
    "Open Settings → Connectors & Apps → Google Health. Keep the Google Cloud console in another tab. The reference panel below shows the callback path, requested read permissions, supported destinations, and default sync interval from this running version.",
    "Imported steps, heart rate, and sleep appear in their reports. Weight appears in Patient Record → Weight history. The supported-destinations table below follows the connector's actual capability catalogue.",
    "The phone/watch must first sync to Google. Local Health Connect records do not guarantee availability in Google's cloud API. Inventory availability, Google sign-in, and import completion are separate states. Unsupported types can be visible but cannot be selected for import.",
    [
      "/reports/steps",
      "/reports/heart-rate",
      "/reports/sleep",
      "/settings/weight",
      "/settings/connectors",
    ],
    "google-health"
  );
  google.steps = [
    {
      title: "Check Google access and your source",
      text: "Start with the official Google setup guide linked below and check current API access availability: If Google restricts new project access and your project cannot enable the API, resolve eligibility with Google first. Sign into the Google Health mobile app with the account you want to use and check that it contains recent readings. A successful Google sign-in alone does not establish a Google Health profile.",
    },
    {
      title: "Let phone data reach Google Health",
      text: "For a watch syncing directly with Google Health, verify the readings in that app. For another Android source using Health Connect, allow the source app to write its data and Google Health to read it. In Google Health use Connections → Partner apps → Sync your favorite apps and complete setup, including the history/background permissions you want. Check the linked official instructions if labels change. Newest data may arrive first: wait for Google to finish syncing older records before diagnosing an empty historical report.",
    },
    {
      title: "Create or choose your Google Cloud project",
      text: "Use your own project, not someone else's client credentials. Enable the Google Health API using the official setup guide. The project and OAuth app names are descriptive: choose recognisable names. These names do not need to match Nocturne. Do not enable unrelated APIs merely because a guide lists other integrations.",
    },
    {
      title: "Prepare consent and add your test account",
      text: "In Google Auth Platform configure the app's consent details. For a personal test choose External with publishing status Testing, then Audience → Test users → Add users. Add the exact Google email address whose health data you will connect and save. Under Data Access, add the read scopes listed in this guide's reference panel and save. Nocturne also requests basic sign-in identification automatically. Testing grants can expire after seven days; reconnect when required.",
    },
    {
      title: "Create the Web application client",
      text: "In Clients create an OAuth client of type Web application (Web Server in the quick-start wizard). Copy the Callback URL displayed in Nocturne into Authorized redirect URIs. It must match exactly, including https, hostname, port, path, and any trailing slash. Use Nocturne's address, not the sample google.com redirect in Google's quick start. Save and copy both Client ID and Client secret. The name is descriptive; the credentials and redirect address must be exact.",
    },
    {
      title: "Save credentials and sign in",
      text: "On Nocturne's Google Health page enter Google client ID, Client secret, and Callback URL. The secret is required on first setup; leave it empty only when editing an already-saved secret. Choose Import data from for the earliest history you want, then Save and connect. Sign in with the same test account and review the requested read permissions. Grant the permissions for the data types you want; declining one can leave its rows unavailable. Confirm you return to Nocturne's connector page.",
    },
    {
      title: "Review the inventory and start an import",
      text: "Inspect the available-data table. Found indicates source availability, not data already imported. Select supported types you want, then choose Save selection and import. Save import settings only saves; Refresh inventory only checks availability. Rows marked Not scanned need a refreshed inventory, No permission needs the relevant consent, and No destination yet is unsupported. Leaving the page does not cancel the server's import.",
    },
    {
      title: "Confirm that your reports contain data",
      text: "Watch sync status and Historical import progress. Current data is processed first, followed by older history in stages. Open the relevant report and choose dates that contain source records; check Weight history for weight. If empty, verify the Google account, its actual records, granted permissions, selected types, date range, and last sync error in that order. Use Sync now to request the current selection again; avoid repeatedly disconnecting a working import.",
    },
  ];
  google.sections.push(
    {
      title: "Ongoing sync and historical progress",
      text: "Automatic sync uses the default interval shown below unless your server changes it. Managed imports resume recent data from a saved watermark with an overlap, then work backwards through older calendar months towards Import data from. Large or failing periods can be split into smaller windows. Progress describes stages and pages, not an exact remaining time. A restart resumes the saved cursor. Completed older months are not continually rescanned for data that Google uploads late.",
    },
    {
      title: "Data appeared in Google after an older month was completed",
      text: "First confirm that Google now contains the records. Refresh inventory checks availability but does not refill a completed month. Ask a platform administrator to use Import recovery → Open connector reset, select the correct tenant and earliest date to re-read, and monitor the job. The normal import path updates records with duplicate protection. Do not delete all data to solve an empty date range.",
    },
    {
      title: "Common setup errors",
      text: "redirect_uri_mismatch: copy the complete Nocturne Callback URL into the same OAuth client's redirect list. Access blocked or test-user errors: check Audience, exact email, enabled API, and current Google access eligibility. ACCOUNT_NOT_LINKED: complete the Google Health app profile with this account. Missing permission: sign in again and grant the required read access. An expired test grant: reconnect; production verification and quota rules belong to Google and can change.",
    },
    {
      title: "Pause, disconnect, and deletion",
      text: "Clear selected types and Save import settings to pause imports without deleting history. Disconnect removes the connection but keeps imported records. Delete imported Google data is a separate confirmed action available after disconnecting; it affects this tenant's imported records, not Google's originals. Credentials are stored in the server's encrypted connector configuration. Never paste the secret or token into support conversations.",
    }
  );
  google.sources = [
    {
      title: "Google: API setup, access availability, and account linking",
      href: "https://developers.google.com/health/setup",
    },
    {
      title: "Google: connect Health Connect to Google Health",
      href: "https://support.google.com/googlehealth/answer/14506680",
    },
    {
      title: "Google: Web application OAuth and exact redirects",
      href: "https://developers.google.com/identity/protocols/oauth2/web-server",
    },
    {
      title: "Google: API data types and availability",
      href: "https://developers.google.com/health/data-types",
    },
  ];
  const connector = (
    id: string,
    title: string,
    source: string,
    details: string
  ) =>
    guide(
      `/settings/connectors/${id}`,
      title,
      `Connect ${title} to the current Nocturne tenant.`,
      source,
      "Open this connector under Settings → Connectors & Apps. Follow its generated configuration form and field descriptions, choose supported data types, save, and inspect connection/sync status. The form's validation and capability list are authoritative for this version.",
      "Successfully imported records are used by the corresponding charts, reports, and histories. Inspect the connector's data-type list before choosing it.",
      `${details} Server configuration, account permissions, and provider availability affect syncing. A saved configuration is not proof an import succeeded. Keep credentials private.`,
      ["/settings/connectors"]
    );
  return [
    google,
    connector(
      "dexcom",
      "Dexcom",
      "Glucose available through your Dexcom Share account.",
      "The account must support sharing; check the selected region and credentials in the setup form. This is a cloud connection, so readings depend on the original device uploading."
    ),
    connector(
      "libre",
      "Libre",
      "Glucose shared through the configured LibreLinkUp account.",
      "Check the account, region, and linked patient's available readings. Data visible locally on a sensor is not necessarily available to the sharing service."
    ),
    connector(
      "carelink",
      "CareLink",
      "Records made available by the connected CareLink account.",
      "Check the authentication and account requirements shown by this connector. Pump uploads and provider authentication can delay availability."
    ),
    connector(
      "glooko",
      "Glooko",
      "Supported data uploaded to your Glooko account.",
      "Check the source account's date range and device uploads. Imported profiles and records can have different coverage."
    ),
    connector(
      "nightscout",
      "Nightscout",
      "Records read from the Nightscout server you configure.",
      "Use the correct source URL and authorised credential. Ongoing connector sync is different from the one-off Data Migration tool."
    ),
    connector(
      "tandem",
      "Tandem",
      "Supported pump data from the configured Tandem account.",
      "Choose the appropriate account/region settings shown in the form. Pump upload timing and available historical profile information affect reports."
    ),
    connector(
      "mylife",
      "myLife",
      "Supported records from your myLife account.",
      "Check credentials and actual source uploads. The connector's capability list shows which records this version imports."
    ),
    connector(
      "gluroo",
      "Gluroo",
      "Glucose, treatment, and profile records made available through Gluroo Global Connect.",
      "Check the Global Connect setup and source permissions shown by the connector. Uploaded data must reach that account before it can be imported."
    ),
    connector(
      "eversense",
      "Eversense",
      "Supported readings from the configured Eversense account.",
      "Check the source account, region, and available cloud uploads. Historical coverage depends on the provider."
    ),
    connector(
      "myfitnesspal",
      "MyFitnessPal",
      "Supported nutrition and meal records available through the configured MyFitnessPal connection.",
      "Check the supported data-type list and source account; this is not a glucose sensor connection."
    ),
    connector(
      "tidepool",
      "Tidepool",
      "Supported device records uploaded to the configured Tidepool account.",
      "Verify the selected source account and device uploads. Source records and profiles can have different available periods."
    ),
    connector(
      "twiist",
      "twiist",
      "Supported pump data available from the configured twiist service.",
      "Follow the connection's authentication fields and capability list. Delivery information depends on successful pump uploads."
    ),
    connector(
      "nocturne",
      "Nocturne Remote",
      "Records available from the remote Nocturne instance you configure.",
      "Verify the remote URL, authorised token, tenant, and selected data types. This is a data connection, not a copy of all server settings."
    ),
    connector(
      "homeassistant",
      "Home Assistant",
      "The Home Assistant integration and entities configured for this connection.",
      "Follow the Setup Guide on the connector page for HACS and integration setup. This integration is separate from installing the Nocturne HA app itself."
    ),
  ];
}
