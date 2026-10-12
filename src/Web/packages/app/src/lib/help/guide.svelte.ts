import type { HelpArticle } from "./documentation-types";

export function guide(
  href: string,
  title: string,
  introduction: string,
  source: string,
  settings: string,
  used: string,
  limits: string,
  related: string[] = [],
  reference?: HelpArticle["reference"]
): HelpArticle {
  return {
    href,
    title,
    introduction,
    sections: [
      { title: "Where the information comes from", text: source },
      { title: "Settings and controls", text: settings },
      { title: "Where this information is used", text: used },
      { title: "Limits and missing information", text: limits },
    ],
    related,
    reference,
  };
}
