export const HttpMethod = {
  Get: "GET",
  Post: "POST",
} as const;

export type HttpMethod = (typeof HttpMethod)[keyof typeof HttpMethod];
