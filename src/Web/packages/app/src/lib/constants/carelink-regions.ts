export const CareLinkRegion = {
  Eu: "EU",
  Us: "US",
} as const;

export type CareLinkRegion = (typeof CareLinkRegion)[keyof typeof CareLinkRegion];
