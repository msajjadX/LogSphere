import { api } from '../api/client';

/**
 * SupportHub embed — loads the SDK from the SupportHub origin and wires it to
 * LogSphere's own launch endpoint.
 *
 * The SDK's built-in `launchEndpoint` fetch carries only cookies, but LogSphere's
 * session is a bearer token in localStorage — so the token is minted through our
 * authenticated api client via the SDK's `getLaunchToken` hook instead. The
 * integration secret never reaches this bundle; the server mints the launch
 * token and returns only that.
 */

export type SupportContext = {
  module?: string;
  page?: string;
  recordRef?: string;
  requestId?: string;
};

type SupportHubSdk = {
  init(config: {
    baseUrl: string;
    launcher: boolean;
    appVersion?: string;
    getContext: () => SupportContext;
    getLaunchToken: (ctx: SupportContext) => Promise<string | null>;
    onLaunchError?: (message: string) => void;
    onTicketCreated?: (reference: string) => void;
  }): void;
  open(): void;
  close(): void;
  attach(element: HTMLElement): void;
};

/** Fired on window when opening support fails; detail is the human-readable reason. */
export const SUPPORT_ERROR_EVENT = 'logsphere:support-error';

declare global {
  interface Window {
    SupportHub?: SupportHubSdk;
  }
}

let ready: Promise<boolean> | null = null;

/**
 * Idempotent (safe under strict-mode double effects): the first call decides,
 * later calls share the same promise. Resolves false when support is not
 * configured server-side or the SDK cannot be loaded — callers hide the button.
 */
export function initSupportHub(getContext: () => SupportContext): Promise<boolean> {
  ready ??= doInit(getContext).catch(() => false);
  return ready;
}

export function openSupportPanel(): void {
  window.SupportHub?.open();
}

async function doInit(getContext: () => SupportContext): Promise<boolean> {
  const cfg = await api.get<{ enabled: boolean; widgetUrl: string | null }>('/support/config');
  if (!cfg.enabled || !cfg.widgetUrl) return false;

  const origin = cfg.widgetUrl.replace(/\/$/, '');
  // The version query is a cache-bust: SupportHub deployments briefly served the
  // SDK with an immutable cache header, and browsers that cached it under the
  // bare URL would otherwise keep that copy for a year.
  await loadScript(`${origin}/supporthub-sdk.js?v=${encodeURIComponent(__APP_VERSION__)}`);
  if (!window.SupportHub) return false;

  window.SupportHub.init({
    baseUrl: origin,
    launcher: false, // LogSphere renders its own header button
    appVersion: __APP_VERSION__,
    getContext,
    // A fresh single-use token on every open; the api client attaches the JWT
    // and unwraps LogSphere's response envelope.
    getLaunchToken: (ctx) => api.post<{ launchToken: string }>('/support/launch', ctx ?? {}).then((d) => d.launchToken),
    // The SDK closes the panel instead of showing a dead widget; surface the
    // server's reason (e.g. "your account has no email address") in the shell.
    onLaunchError: (message) => window.dispatchEvent(new CustomEvent(SUPPORT_ERROR_EVENT, { detail: message })),
  });
  return true;
}

function loadScript(src: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = src;
    script.async = true;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error(`Failed to load ${src}`));
    document.head.appendChild(script);
  });
}
