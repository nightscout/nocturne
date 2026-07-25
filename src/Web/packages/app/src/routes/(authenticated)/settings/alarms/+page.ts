import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

// Legacy alarms page; alarms are configured under Alerts.
export const load: PageLoad = async () => {
  redirect(308, "/alerts");
};
