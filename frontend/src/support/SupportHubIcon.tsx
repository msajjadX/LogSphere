import { supportIconMarkup } from './supporthub';

/**
 * The SupportHub headset mark for LogSphere's own support button.
 *
 * The artwork comes from the SDK (`SupportHub.icon()`), so when SupportHub
 * changes its logo this button follows on the next SDK load without a change
 * here. Drawn in `currentColor`, so it takes the button's colour like the
 * lucide icons beside it.
 *
 * The markup is injected because the SDK hands it over as a string. It is not
 * host input: it comes from the SupportHub script this page already loads and
 * executes, and the SDK escapes the className it is given.
 *
 * The JSX below is a fallback for SupportHub deployments older than the SDK's
 * `icon()` (added 2026-07). Once every environment is past that, delete it and
 * return null instead.
 */
export function SupportHubIcon({ className }: { className?: string }) {
  const markup = supportIconMarkup(className);

  if (markup) {
    return <span style={{ display: 'inline-flex' }} dangerouslySetInnerHTML={{ __html: markup }} />;
  }

  return (
    <svg viewBox="0 0 48 48" fill="none" aria-hidden="true" className={className}>
      <path
        d="M9 26v-4.5a15 15 0 0 1 30 0V26"
        stroke="currentColor"
        strokeWidth="4.5"
        strokeLinecap="round"
      />
      <rect x="4.5" y="22" width="10" height="16" rx="5" fill="currentColor" />
      <rect x="33.5" y="22" width="10" height="16" rx="5" fill="currentColor" />
      <path
        d="M38.5 38a7.5 7.5 0 0 1-7.5 7h-4"
        stroke="currentColor"
        strokeWidth="3.5"
        strokeLinecap="round"
      />
      <circle cx="25" cy="45" r="2.8" fill="currentColor" />
    </svg>
  );
}
