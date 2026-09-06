export interface BlogPostMeta {
  title: string;
  slug: string;
  date: string;
  tags: string[];
  category: string;
  author: string;
  summary: string;
  image?: string;
  /** Excluded from the manifest entirely in production, so it is never built. */
  draft?: boolean;
  /**
   * Built and reachable at its URL, but kept off every index, feed and sitemap.
   * For circulating a finished post privately (proofreading, review) before it
   * is announced. Unlike `draft`, it stays in the manifest so the page renders.
   */
  unlisted?: boolean;
}

export interface BlogManifest {
  posts: BlogPostMeta[];
  tags: string[];
  categories: string[];
}
