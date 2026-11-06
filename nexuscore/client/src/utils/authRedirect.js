const AUTH_PAGES = new Set(['/login', '/register']);

/**
 * Where to send the user after logging in or registering: the page that bounced them to /login
 * (stored by ProtectedRoute in `location.state.from`), or `fallback` if there is none.
 *
 * Only same-app paths are accepted, so a crafted history state can never redirect off-site
 * (`//evil.com`, absolute URLs) or loop back into the login/register pages.
 */
export function getRedirectTarget(location, fallback = '/') {
  const from = location?.state?.from;
  const pathname = from?.pathname;
  if (typeof pathname !== 'string') return fallback;
  if (!pathname.startsWith('/') || pathname.startsWith('//')) return fallback;
  if (AUTH_PAGES.has(pathname)) return fallback;
  return `${pathname}${from.search || ''}${from.hash || ''}`;
}
