/** UI-only help structure. Reference values come from the generated API models. */
export interface HelpSection {
  title: string;
  text: string;
  links?: HelpSource[];
}
export interface HelpSource {
  title: string;
  href: string;
}
export interface HelpArticle {
  href: string;
  title: string;
  introduction: string;
  sections: HelpSection[];
  related: string[];
  steps?: HelpSection[];
  sources?: HelpSource[];
  reference?: "glucose" | "google-health";
}
