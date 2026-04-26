import { query } from "$app/server";
import { z } from "zod";

const emptySchema = z.object({});

// GitHub API configuration
const GITHUB_OWNER = "nightscout";
const GITHUB_REPO = "nocturne";
const GITHUB_API_BASE = "https://api.github.com";

// Cache for GitHub data (5 minute TTL)
interface CacheEntry<T> {
  data: T;
  timestamp: number;
}
const CACHE_TTL_MS = 5 * 60 * 1000; // 5 minutes
const githubCache = new Map<string, CacheEntry<unknown>>();

function getCached<T>(key: string): T | null {
  const entry = githubCache.get(key);
  if (!entry) return null;
  if (Date.now() - entry.timestamp > CACHE_TTL_MS) {
    githubCache.delete(key);
    return null;
  }
  return entry.data as T;
}

function setCache<T>(key: string, data: T): void {
  githubCache.set(key, { data, timestamp: Date.now() });
}

// GitHub API schemas
const githubUserSchema = z.object({
  login: z.string(),
  avatar_url: z.string(),
  html_url: z.string(),
});

const githubMilestoneSchema = z.object({
  id: z.number(),
  number: z.number(),
  title: z.string(),
  description: z.string().nullable(),
  state: z.enum(["open", "closed"]),
  open_issues: z.number(),
  closed_issues: z.number(),
  created_at: z.string(),
  updated_at: z.string(),
  due_on: z.string().nullable(),
  closed_at: z.string().nullable(),
  html_url: z.string(),
});

const githubLabelSchema = z.object({
  id: z.number(),
  name: z.string(),
  color: z.string(),
  description: z.string().nullable().optional(),
});

const githubIssueSchema = z.object({
  id: z.number(),
  number: z.number(),
  title: z.string(),
  state: z.enum(["open", "closed"]),
  html_url: z.string(),
  created_at: z.string(),
  updated_at: z.string(),
  closed_at: z.string().nullable(),
  user: githubUserSchema.nullable(),
  labels: z.array(githubLabelSchema),
  assignees: z.array(githubUserSchema),
  pull_request: z.object({}).optional(),
});

export type GitHubMilestone = z.infer<typeof githubMilestoneSchema>;
export type GitHubIssue = z.infer<typeof githubIssueSchema>;
export type GitHubLabel = z.infer<typeof githubLabelSchema>;

export interface RoadmapMilestone extends GitHubMilestone {
  issues: GitHubIssue[];
  progress: number;
}

// Fetch milestones from GitHub
export const getRoadmapData = query(emptySchema, async () => {
  const cacheKey = "roadmap-data";
  const cached = getCached<RoadmapMilestone[]>(cacheKey);
  if (cached) {
    return cached;
  }

  const headers: HeadersInit = {
    Accept: "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
    "User-Agent": "Nocturne-Portal",
  };

  // Fetch all milestones (open and closed)
  const milestonesResponse = await fetch(
    `${GITHUB_API_BASE}/repos/${GITHUB_OWNER}/${GITHUB_REPO}/milestones?state=all&sort=due_on&direction=asc&per_page=100`,
    { headers, signal: AbortSignal.timeout(10000) }
  );

  if (!milestonesResponse.ok) {
    throw new Error(`Failed to fetch milestones: ${milestonesResponse.status}`);
  }

  const milestonesData: unknown = await milestonesResponse.json();
  const milestones = z.array(githubMilestoneSchema).parse(milestonesData);

  // Fetch issues for each milestone
  const roadmapMilestones: RoadmapMilestone[] = await Promise.all(
    milestones.map(async (milestone) => {
      const issuesResponse = await fetch(
        `${GITHUB_API_BASE}/repos/${GITHUB_OWNER}/${GITHUB_REPO}/issues?milestone=${milestone.number}&state=all&per_page=100`,
        { headers, signal: AbortSignal.timeout(10000) }
      );

      let issues: GitHubIssue[] = [];
      if (issuesResponse.ok) {
        const issuesData: unknown = await issuesResponse.json();
        const allIssues = z.array(githubIssueSchema).parse(issuesData);
        // Filter out pull requests (issues endpoint includes PRs)
        issues = allIssues.filter((issue) => !issue.pull_request);
      }

      const totalIssues = milestone.open_issues + milestone.closed_issues;
      const progress = totalIssues > 0 ? (milestone.closed_issues / totalIssues) * 100 : 0;

      return {
        ...milestone,
        issues,
        progress,
      };
    })
  );

  setCache(cacheKey, roadmapMilestones);
  return roadmapMilestones;
});

// Changelog types
const githubReleaseSchema = z.object({
  id: z.number(),
  tag_name: z.string(),
  name: z.string().nullable(),
  body: z.string().nullable(),
  published_at: z.string().nullable(),
  html_url: z.string(),
  author: githubUserSchema.nullable(),
  draft: z.boolean(),
  prerelease: z.boolean(),
});

export type ChangelogRelease = z.infer<typeof githubReleaseSchema>;

// Community data schemas
const githubContributorSchema = z.object({
  login: z.string(),
  avatar_url: z.string(),
  html_url: z.string(),
  contributions: z.number(),
});

const githubRepoSchema = z.object({
  stargazers_count: z.number(),
  forks_count: z.number(),
});

export type GitHubContributor = z.infer<typeof githubContributorSchema>;

const changelogInputSchema = z.object({
  page: z.number().optional().default(1),
  per_page: z.number().optional().default(30),
});

// Fetch releases from GitHub
export const getChangelog = query(changelogInputSchema, async ({ page, per_page }) => {
  const cacheKey = `changelog-page-${page}-${per_page}`;
  const cached = getCached<ChangelogRelease[]>(cacheKey);
  if (cached) {
    return cached;
  }

  const headers: HeadersInit = {
    Accept: "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
    "User-Agent": "Nocturne-Portal",
  };

  const response = await fetch(
    `${GITHUB_API_BASE}/repos/${GITHUB_OWNER}/${GITHUB_REPO}/releases?per_page=${per_page}&page=${page}`,
    { headers, signal: AbortSignal.timeout(10000) }
  );

  if (!response.ok) {
    throw new Error(`Failed to fetch releases: ${response.status}`);
  }

  const data: unknown = await response.json();
  const releases = z.array(githubReleaseSchema).parse(data);

  // Filter out drafts
  const published = releases.filter((r) => !r.draft);

  setCache(cacheKey, published);
  return published;
});

// Fetch community data (repo stats, contributors, latest release)
export const getCommunityData = query(emptySchema, async () => {
  const cacheKey = "community-data";
  const cached = getCached<{
    stars: number;
    forks: number;
    contributors: GitHubContributor[];
    latestRelease: string | null;
  }>(cacheKey);
  if (cached) {
    return cached;
  }

  const headers: HeadersInit = {
    Accept: "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
    "User-Agent": "Nocturne-Portal",
  };

  const [repoResponse, contributorsResponse, releasesResponse] =
    await Promise.all([
      fetch(`${GITHUB_API_BASE}/repos/${GITHUB_OWNER}/${GITHUB_REPO}`, {
        headers,
        signal: AbortSignal.timeout(10000),
      }),
      fetch(
        `${GITHUB_API_BASE}/repos/${GITHUB_OWNER}/${GITHUB_REPO}/contributors?per_page=100`,
        { headers, signal: AbortSignal.timeout(10000) }
      ),
      fetch(
        `${GITHUB_API_BASE}/repos/${GITHUB_OWNER}/${GITHUB_REPO}/releases?per_page=1`,
        { headers, signal: AbortSignal.timeout(10000) }
      ),
    ]);

  if (!repoResponse.ok || !contributorsResponse.ok) {
    throw new Error(
      `Failed to fetch community data: repo=${repoResponse.status} contributors=${contributorsResponse.status}`
    );
  }

  const repoData = githubRepoSchema.parse(await repoResponse.json());
  const contributorsData = z
    .array(githubContributorSchema)
    .parse(await contributorsResponse.json());

  let latestRelease: string | null = null;
  if (releasesResponse.ok) {
    const releases = z
      .array(githubReleaseSchema)
      .parse(await releasesResponse.json());
    const published = releases.find((r) => !r.draft);
    latestRelease = published?.tag_name ?? null;
  }

  const result = {
    stars: repoData.stargazers_count,
    forks: repoData.forks_count,
    contributors: contributorsData,
    latestRelease,
  };

  setCache(cacheKey, result);
  return result;
});
