export function parseCalendarDate(date: string): Date {
  const [year, month, day] = date.split("-").map(Number);

  return new Date(year, month - 1, day);
}

export function getCalendarDayNumber(date: string): number {
  return parseCalendarDate(date).getDate();
}

export function formatCalendarDate(
  date: string,
  locales: Intl.LocalesArgument,
  options: Intl.DateTimeFormatOptions
): string {
  return parseCalendarDate(date).toLocaleDateString(locales, options);
}
