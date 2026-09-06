import { getRequestEvent, form } from "$app/server";
import { error, redirect } from "@sveltejs/kit";

export {
  getFallbackUrl,
  getSupportConfig,
} from "$api/generated/supports.generated.remote";

/**
 * Shape of the issue form submission. Field names match the `name` attributes
 * in IssueCreatorDialog; `images[]` arrives as a File array. A type alias
 * (not an interface) so it satisfies the RemoteFormInput index constraint.
 */
type IssueFormInput = {
  channel?: "github" | "operator";
  template: string;
  title: string;
  description: string;
  stepsToReproduce?: string;
  expectedBehavior?: string;
  actualBehavior?: string;
  cgmSource?: string;
  timeRange?: string;
  diagnosticInfo: string;
  images?: File[];
};

/**
 * Submit a support issue with optional screenshot attachments.
 *
 * This is a `form` remote (not a `command`) because the payload carries File
 * objects: command arguments are devalue-serialised, which cannot represent a
 * File, whereas form submissions transport files natively.
 */
export const submitIssue = form("unchecked", async (data: IssueFormInput) => {
  const { apiClient } = getRequestEvent().locals;

  if (!data.title?.trim() || !data.description?.trim()) {
    throw error(400, "Title and description are required");
  }

  // Files arrive as SvelteKit's lazy file proxies; materialise them into real
  // File objects so undici's multipart encoder accepts them.
  const images = await Promise.all(
    (data.images ?? []).map(
      async (f) => new File([await f.arrayBuffer()], f.name, { type: f.type })
    )
  );

  if (data.channel === "operator") {
    // Resolve operator URL server-side to prevent SSRF via client-supplied URLs
    const config = await apiClient.support.getSupportConfig();
    const url = config.accountBilling?.url;
    if (!url) throw error(400, "Operator support not configured");

    const formData = new FormData();
    formData.append("template", data.template);
    formData.append("title", data.title);
    formData.append("description", data.description);
    if (data.stepsToReproduce)
      formData.append("stepsToReproduce", data.stepsToReproduce);
    if (data.expectedBehavior)
      formData.append("expectedBehavior", data.expectedBehavior);
    if (data.actualBehavior)
      formData.append("actualBehavior", data.actualBehavior);
    if (data.cgmSource) formData.append("cgmSource", data.cgmSource);
    if (data.timeRange) formData.append("timeRange", data.timeRange);
    formData.append("diagnosticInfo", data.diagnosticInfo);
    for (const image of images) {
      formData.append("images", image, image.name);
    }

    const response = await fetch(url, {
      body: formData,
      method: "POST",
      headers: { Accept: "application/json" },
    });

    if (!response.ok) {
      console.error(
        `Error in support.submitIssue: operator endpoint returned ${response.status}`
      );
      throw error(502, "Operator support endpoint rejected the issue");
    }

    return await response.json();
  }

  try {
    // The generated remote can't model this endpoint's multipart file upload,
    // so call the NSwag client method directly (it builds the multipart body).
    return await apiClient.support.createIssue(
      data.template,
      data.title,
      data.description,
      data.stepsToReproduce || undefined,
      data.expectedBehavior || undefined,
      data.actualBehavior || undefined,
      data.cgmSource || undefined,
      data.timeRange || undefined,
      data.diagnosticInfo,
      images.map((file) => ({ data: file, fileName: file.name }))
    );
  } catch (err) {
    const status = (err as { status?: number })?.status;
    if (status === 401) {
      const { url } = getRequestEvent();
      throw redirect(
        302,
        `/auth/login?returnUrl=${encodeURIComponent(url.pathname + url.search)}`
      );
    }
    if (status === 403) throw error(403, "Forbidden");
    console.error("Error in support.submitIssue:", err);
    throw error(502, "Failed to create issue");
  }
});
