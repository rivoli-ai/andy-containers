import { Pipe, PipeTransform } from '@angular/core';

/**
 * Formats a date string or Date as a human-readable uptime duration from now.
 * Usage: {{ container.startedAt | uptime }}
 * Output: "2m", "3h 15m", "1d 4h", "2mo 5d", "1y 3mo"
 */
@Pipe({ name: 'uptime', standalone: true, pure: false })
export class UptimePipe implements PipeTransform {
  transform(value: string | Date | null | undefined): string {
    if (!value) return '--';
    const start = typeof value === 'string' ? new Date(value) : value;
    const now = new Date();
    const diffMs = now.getTime() - start.getTime();
    if (diffMs < 0) return '--';
    return formatDuration(diffMs);
  }
}

export function formatDuration(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);
  const days = Math.floor(hours / 24);
  const months = Math.floor(days / 30);
  const years = Math.floor(days / 365);

  if (years > 0) {
    const remMonths = Math.floor((days - years * 365) / 30);
    return remMonths > 0 ? `${years}y ${remMonths}mo` : `${years}y`;
  }
  if (months > 0) {
    const remDays = days - months * 30;
    return remDays > 0 ? `${months}mo ${remDays}d` : `${months}mo`;
  }
  if (days > 0) {
    const remHours = hours - days * 24;
    return remHours > 0 ? `${days}d ${remHours}h` : `${days}d`;
  }
  if (hours > 0) {
    const remMinutes = minutes - hours * 60;
    return remMinutes > 0 ? `${hours}h ${remMinutes}m` : `${hours}h`;
  }
  if (minutes > 0) {
    return `${minutes}m`;
  }
  return `${seconds}s`;
}
