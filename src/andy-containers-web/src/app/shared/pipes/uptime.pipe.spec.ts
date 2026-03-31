import { UptimePipe, formatDuration } from './uptime.pipe';

describe('UptimePipe', () => {
  let pipe: UptimePipe;

  beforeEach(() => {
    pipe = new UptimePipe();
  });

  it('should create', () => {
    expect(pipe).toBeTruthy();
  });

  it('should return -- for null/undefined', () => {
    expect(pipe.transform(null)).toBe('--');
    expect(pipe.transform(undefined)).toBe('--');
    expect(pipe.transform('')).toBe('--');
  });

  it('should return -- for future dates', () => {
    const future = new Date(Date.now() + 60000).toISOString();
    expect(pipe.transform(future)).toBe('--');
  });

  it('should format seconds', () => {
    const start = new Date(Date.now() - 30 * 1000).toISOString();
    expect(pipe.transform(start)).toBe('30s');
  });

  it('should format minutes', () => {
    const start = new Date(Date.now() - 5 * 60 * 1000).toISOString();
    expect(pipe.transform(start)).toBe('5m');
  });

  it('should format hours and minutes', () => {
    const start = new Date(Date.now() - (3 * 60 + 15) * 60 * 1000).toISOString();
    expect(pipe.transform(start)).toBe('3h 15m');
  });

  it('should format days and hours', () => {
    const start = new Date(Date.now() - (2 * 24 + 6) * 60 * 60 * 1000).toISOString();
    expect(pipe.transform(start)).toBe('2d 6h');
  });

  it('should accept Date objects', () => {
    const start = new Date(Date.now() - 10 * 60 * 1000);
    expect(pipe.transform(start)).toBe('10m');
  });
});

describe('formatDuration', () => {
  it('should format 0ms as 0s', () => {
    expect(formatDuration(0)).toBe('0s');
  });

  it('should format seconds only', () => {
    expect(formatDuration(45 * 1000)).toBe('45s');
  });

  it('should format exact minutes', () => {
    expect(formatDuration(5 * 60 * 1000)).toBe('5m');
  });

  it('should format hours with remaining minutes', () => {
    expect(formatDuration((2 * 60 + 30) * 60 * 1000)).toBe('2h 30m');
  });

  it('should format exact hours', () => {
    expect(formatDuration(4 * 60 * 60 * 1000)).toBe('4h');
  });

  it('should format days with remaining hours', () => {
    expect(formatDuration((3 * 24 + 12) * 60 * 60 * 1000)).toBe('3d 12h');
  });

  it('should format exact days', () => {
    expect(formatDuration(7 * 24 * 60 * 60 * 1000)).toBe('7d');
  });

  it('should format months with remaining days', () => {
    expect(formatDuration(45 * 24 * 60 * 60 * 1000)).toBe('1mo 15d');
  });

  it('should format exact months', () => {
    expect(formatDuration(60 * 24 * 60 * 60 * 1000)).toBe('2mo');
  });

  it('should format years with remaining months', () => {
    expect(formatDuration(400 * 24 * 60 * 60 * 1000)).toBe('1y 1mo');
  });

  it('should format exact years', () => {
    expect(formatDuration(365 * 24 * 60 * 60 * 1000)).toBe('1y');
  });

  it('should format multiple years', () => {
    expect(formatDuration(800 * 24 * 60 * 60 * 1000)).toBe('2y 2mo');
  });

  // Edge cases
  it('should format sub-second durations as 0s', () => {
    expect(formatDuration(500)).toBe('0s');
    expect(formatDuration(999)).toBe('0s');
  });

  it('should format exactly 1 second', () => {
    expect(formatDuration(1000)).toBe('1s');
  });

  it('should format exactly 59 seconds without rolling to minutes', () => {
    expect(formatDuration(59 * 1000)).toBe('59s');
  });

  it('should format exactly 1 minute', () => {
    expect(formatDuration(60 * 1000)).toBe('1m');
  });

  it('should format exactly 1 hour', () => {
    expect(formatDuration(60 * 60 * 1000)).toBe('1h');
  });

  it('should format exactly 1 day', () => {
    expect(formatDuration(24 * 60 * 60 * 1000)).toBe('1d');
  });

  it('should format exactly 30 days as 1 month', () => {
    expect(formatDuration(30 * 24 * 60 * 60 * 1000)).toBe('1mo');
  });

  it('should format exactly 365 days as 1 year with no remaining months', () => {
    expect(formatDuration(365 * 24 * 60 * 60 * 1000)).toBe('1y');
  });

  it('should format 1 millisecond as 0s', () => {
    expect(formatDuration(1)).toBe('0s');
  });

  it('should handle large durations (10 years)', () => {
    const tenYears = 10 * 365 * 24 * 60 * 60 * 1000;
    expect(formatDuration(tenYears)).toBe('10y');
  });

  it('should format 29 days as days not months', () => {
    expect(formatDuration(29 * 24 * 60 * 60 * 1000)).toBe('29d');
  });

  it('should format hours without minutes when exact', () => {
    expect(formatDuration(3 * 60 * 60 * 1000)).toBe('3h');
  });

  it('should format days without hours when exact', () => {
    expect(formatDuration(5 * 24 * 60 * 60 * 1000)).toBe('5d');
  });

  it('should format months without days when exact', () => {
    expect(formatDuration(90 * 24 * 60 * 60 * 1000)).toBe('3mo');
  });
});
