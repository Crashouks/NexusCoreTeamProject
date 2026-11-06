import { createContext, useContext, useState, useEffect, useCallback, useRef } from 'react';
import { api, UNAUTHORIZED_EVENT } from '../api/api';
import { useToast } from '../components/Toast';

const AuthContext = createContext(null);

function userFromProfile(p) {
  if (!p) return null;
  return {
    userId: p.user_id,
    username: p.username,
    role: p.role,
    cloudPlan: p.cloud_plan,
  };
}

/** Don't re-verify on tab focus more often than this. */
const REVERIFY_MIN_INTERVAL_MS = 30_000;

/**
 * Global auth state (Context API). Everything that reads `useAuth()` — the header, ProtectedRoute, the pages —
 * re-renders together when it changes, so login/logout/expiry update the whole UI with no reload.
 *
 * The JWT is an HttpOnly cookie that JavaScript cannot read, so "is the token valid?" is answered by the server:
 *   - on startup:           GET /users/me
 *   - on any 401:           api.js fires UNAUTHORIZED_EVENT -> re-check /users/me, and sign out if it is a 401 too
 *   - when the tab returns: same re-check (throttled), which also catches a logout done in another tab
 */
export function AuthProvider({ children }) {
  const [user, setUser] = useState(null);
  const [profile, setProfile] = useState(null);
  const [library, setLibrary] = useState([]);
  const [loading, setLoading] = useState(true);
  const { showToast } = useToast();

  const userRef = useRef(null);
  const verifyingRef = useRef(false);
  const lastVerifiedRef = useRef(0);
  const showToastRef = useRef(showToast);
  useEffect(() => { userRef.current = user; }, [user]);
  useEffect(() => { showToastRef.current = showToast; }, [showToast]);

  const clearAuth = useCallback(() => {
    setUser(null);
    setProfile(null);
    setLibrary([]);
  }, []);

  /** Loads profile + library and publishes them together, so no page ever sees a half-signed-in state. */
  const loadProfile = useCallback(async () => {
    try {
      const p = await api.users.me();
      const lib = await api.users.library(p.user_id);
      setProfile(p);
      setUser(userFromProfile(p));
      setLibrary(lib);
      return true;
    } catch {
      clearAuth();
      return false;
    }
  }, [clearAuth]);

  useEffect(() => {
    loadProfile().finally(() => setLoading(false));
    localStorage.removeItem('nc_token');
  }, [loadProfile]);

  const verifySession = useCallback(async () => {
    if (!userRef.current || verifyingRef.current) return;
    verifyingRef.current = true;
    lastVerifiedRef.current = Date.now();
    try {
      await api.users.me();
    } catch (err) {
      // Only a 401 proves the session is gone; a network blip or 5xx must not sign anyone out.
      if (err.status === 401) {
        clearAuth();
        showToastRef.current?.('Your session has expired. Please log in again.', 'info');
      }
    } finally {
      verifyingRef.current = false;
    }
  }, [clearAuth]);

  useEffect(() => {
    const onUnauthorized = () => verifySession();
    const onVisible = () => {
      if (document.visibilityState === 'visible' && Date.now() - lastVerifiedRef.current > REVERIFY_MIN_INTERVAL_MS) {
        verifySession();
      }
    };
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [verifySession]);

  // After a successful login/register the server has set the cookie; load the profile through it. If that fails the
  // browser did not keep the cookie (blocked cookies, wrong API/origin setup) — say so instead of "logging in" to nothing.
  const establishSession = async () => {
    localStorage.removeItem('nc_token');
    localStorage.removeItem('nv_user');
    if (!(await loadProfile())) {
      throw new Error('Signed in, but the session could not be started. Check that your browser accepts cookies from this site.');
    }
  };

  const login = async (email, password) => {
    const data = await api.auth.login(email, password);
    await establishSession();
    return data;
  };

  const register = async (username, email, password) => {
    const data = await api.auth.register(username, email, password);
    await establishSession();
    return data;
  };

  const logout = async () => {
    try {
      await api.auth.logout();
    } catch { /* ignore */ }
    localStorage.removeItem('nc_token');
    localStorage.removeItem('nv_user');
    clearAuth();
  };

  const refreshLibrary = async () => {
    if (profile) {
      const lib = await api.users.library(profile.user_id);
      setLibrary(lib);
    }
  };

  const refreshProfile = async () => {
    await loadProfile();
  };

  const ownsGame = (gameId) => library.some(g => g.game_id === gameId);

  return (
    <AuthContext.Provider value={{
      user, profile, library, loading, login, register, logout,
      refreshLibrary, refreshProfile, ownsGame, isAuth: !!user,
    }}>
      {children}
    </AuthContext.Provider>
  );
}

export const useAuth = () => useContext(AuthContext);
