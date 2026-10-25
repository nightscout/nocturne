import { guide } from "./guide.svelte";
import type { HelpArticle } from "./documentation-types";

export function connectorArticles(): HelpArticle[] {
  const google = guide(
    "/settings/connectors/google-health",
    "Google Health",
    "First install the Google Health app and make sure you can see the data you want to share there. Then follow the Google setup wizard and connect that same account to Nocturne.",
    "Your device or source app sends data to Google Health. Nocturne reads the selected account through Google's API and stores supported records in this tenant's existing histories. A Google Cloud project provides permission to use the API. It does not generate health data.",
    "Open Settings → Connectors & Apps → Google Health. Keep the Google Cloud console in another tab. The reference panel below shows the callback path, requested read permissions, supported destinations, and default sync interval from this running version.",
    "Imported steps, heart rate, and sleep appear in their reports. Weight appears in Patient Record → Weight history. The supported-destinations table below follows the connector's actual capability catalog.",
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
      title: "Install Google Health and check your data",
      text: "Install Google Health (formerly Fitbit) using the Play Store link below. Open it, sign in with the Google account you want to share, and complete its profile setup. Before continuing, make sure the steps, heart rate, sleep, or weight you want to share are visible in that app. Signing into Google alone is not enough.",
      links: [
        {
          title: "Install Google Health from the Play Store",
          href: "https://play.google.com/store/apps/details?id=com.fitbit.FitbitMobile&hl=en",
        },
      ],
    },
    {
      title: "Bring data from other apps into Google Health (optional)",
      text: "On Android use Health Connect, Google's on-device data-sharing service. Let your source app write the desired records and let Google Health read them. In Google Health open Connections → Partner apps → Sync your favorite health apps → Set up. Review the data, history, and background permissions, then check that records appear in Google Health. Garmin needs its watch to sync to Garmin Connect first; Google Health does not connect directly to the watch. Google also documents Samsung Galaxy, Xiaomi/Mi Fitness, Oura, Whoop, Strava, and MyFitnessPal connections. On iPhone the route is generally Apple Health. Available types differ by app: a direct Strava connection exports Google Health activities to Strava. Importing Strava activities uses Health Connect or Apple Health instead. Google Health supports more types than this Nocturne connector, so check the importable-type table below.",
      links: [
        {
          title: "Install or open Health Connect",
          href: "https://play.google.com/store/apps/details?id=com.google.android.apps.healthdata&hl=en",
        },
        {
          title: "Google: set up Health Connect permissions and syncing",
          href: "https://support.google.com/googlehealth/answer/14506680",
        },
        {
          title: "Google: device and app connections, including Garmin",
          href: "https://support.google.com/googlehealth/answer/14236613?hl=en",
        },
      ],
    },
    {
      title: "Open Google's setup wizard",
      text: "Open the Google Setup documentation linked below. Select its blue button labeled Enable the API and get an OAuth 2.0 Client ID. Use a Google account that can manage your Cloud project. If Google restricts access for your project, resolve that eligibility before continuing. Leave Nocturne open in another tab.",
      links: [
        {
          title: "Open the Google Setup documentation",
          href: "https://developers.google.com/health/setup",
        },
      ],
    },
    {
      title: "Name your project and enable the API",
      text: "Choose a new project and enter a project name, or select an existing project you manage. The name is a reminder for you later: any recognisable name is fine. Select Next. Confirm that you want to enable the Google Health API and continue. This enables access to Google's service. It does not create or import any health records.",
    },
    {
      title: "Choose a product name",
      text: "Enter the product/application name requested by the wizard. You are free to choose it: it may match the project name or be completely different. It is the name shown on Google's consent screen when you connect from Nocturne. Complete any required contact details. Neither name needs to match your Nocturne hostname.",
    },
    {
      title: "Create the Web application client",
      text: "For Where are you calling from? select Web Server. In the manual Clients screen the equivalent type is Web application. Copy the complete Callback URL shown by this Nocturne instance under Settings → Connectors & Apps → Google Health into the wizard's redirect field (Authorized redirect URIs). Use your own instance's URL, including its scheme, hostname, port, and callback path exactly. Replace any sample redirect from Google's guide. Select Create. If the wizard's layout changes, the essential choices remain a web client and that exact redirect.",
    },
    {
      title: "Download the client configuration and fill Nocturne's fields",
      text: "Download the Client configuration/Credentials JSON. Copy Client ID and Client secret into the fields of the same names on Nocturne's Google Health page. The secret is required on first setup. Leave it empty only when keeping a secret that was already saved. Check the Callback URL and choose Import data from for the history you want. Keep this form ready while you add the test user in the next step. Store the downloaded file privately: it contains credentials.",
    },
    {
      title: "Add the Google account as a test user",
      text: "Return to the Google Setup documentation, find Add test users, and open its Audience page link. Select the project you just configured. For a personal test use External with publishing status Testing. Under Test users choose Add users, enter the exact email address used in the Google Health app on your phone, and Save. Add each account that will share data. The account managing the Cloud project can be different from the account sharing health data.",
    },
    {
      title: "Check the read permissions in Data Access",
      text: "In Google's Data Access page choose Add or remove scopes, select the Google Health read scopes shown in Nocturne's reference table below, then Update and Save. Nocturne adds basic sign-in identification itself. These scopes govern which records it can read. They do not allow Nocturne to write to your Google Health account.",
    },
    {
      title: "Save and connect with the same account",
      text: "Return to the prepared Nocturne form and choose Save and connect (or Sign in with Google for an existing configuration). Choose the Google Health account you added as a test user, review and grant the read permissions for the data you want, and confirm you return to Nocturne. Testing grants can expire after seven days and then need reconnection.",
    },
    {
      title: "Review the inventory and start an import",
      text: "In the available-data table, Found means records exist at the source, not that Nocturne imported them. Select supported types and choose Save selection and import. Save import settings only saves; Refresh inventory only checks availability. Not scanned needs a refreshed inventory; No permission needs the relevant consent; No destination yet is unsupported. Leaving the page does not cancel the server import.",
    },
    {
      title: "Confirm the result in your reports",
      text: "Watch sync status and Historical import progress. Open Steps, Heart Rate, or Sleep and choose dates containing source records. Use Weight history for weight. If empty, check the source account's visible records, granted permissions, import selection, dates, and last error. Google may still be syncing older phone records. Wait for that to finish before retrying. Completed historical months that receive records later need the recovery procedure below.",
    },
    {
      title: "Personalize branding (optional)",
      text: "After the connection works, open Branding in Google Auth Platform to adjust the app's display name, logo, and available contact or policy details. This changes how Google presents the app. It does not change your health data or Nocturne's import settings. Follow Google's validation requirements for any fields you add.",
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
      // @wc-include
      text: "redirect_uri_mismatch: copy the complete Nocturne Callback URL into the same OAuth client's redirect list. Access blocked or test-user errors: check Audience, exact email, enabled API, and current Google access eligibility. ACCOUNT_NOT_LINKED: complete the Google Health app profile with this account. Missing permission: sign in again and grant the required read access. An expired test grant: reconnect. Production verification and quota rules belong to Google and can change.",
    },
    {
      title: "Pause, disconnect, and deletion",
      text: "Clear selected types and Save import settings to pause imports without deleting history. Disconnect removes the connection but keeps imported records. Delete imported Google data is a separate confirmed action available after disconnecting. It affects this tenant's imported records, not Google's originals. Credentials are stored in the server's encrypted connector configuration. Never paste the secret or token into support conversations.",
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
  ) => ({
    ...guide(
      `/settings/connectors/${id}`,
      title,
      `Connect ${title} to the current Nocturne tenant.`,
      source,
      "Open this connector under Settings → Connectors & Apps. Follow its generated configuration form and field descriptions, choose supported data types, save, and inspect connection/sync status. The form's validation and capability list are authoritative for this version.",
      "Successfully imported records are used by the corresponding charts, reports, and histories. Inspect the connector's data-type list before choosing it.",
      `${details} Server configuration, account permissions, and provider availability affect syncing. A saved configuration is not proof an import succeeded. Keep credentials private.`,
      ["/settings/connectors"]
    ),
    visual: "flow" as const,
    settings: ["/settings/connectors"],
    reading: [
      { title: "Check the source", text: source },
      {
        title: "Complete this connector's form",
        text: "Use its required fields, account/region options, and supported data-type list. Save and inspect the returned connection status.",
      },
      { title: "Verify the imported records", text: details },
    ],
  });
  return [
    google,
    connector(
      "dexcom",
      "Dexcom",
      "Glucose available through your Dexcom Share account.",
      "The account must support sharing. Check the selected region and credentials in the setup form. This is a cloud connection, so readings depend on the original device uploading."
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
      "Use the correct source URL and authorized credential. Ongoing connector sync is different from the one-off Data Migration tool."
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
      "Check the supported data-type list and source account. This is not a glucose sensor connection."
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
      "Verify the remote URL, authorized token, tenant, and selected data types. This is a data connection, not a copy of all server settings."
    ),
    connector(
      "homeassistant",
      "Home Assistant",
      "The Home Assistant integration and entities configured for this connection.",
      "Follow the Setup Guide on the connector page for HACS and integration setup. This integration is separate from installing the Nocturne HA app itself."
    ),
  ];
}
