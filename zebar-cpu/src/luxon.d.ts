// The clock only uses these parts of the Luxon formatter bundled with Zebar.
declare module 'luxon' {
  export class DateTime {
    static fromMillis(milliseconds: number): DateTime;
    toFormat(format: string): string;
  }
}
