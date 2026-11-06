import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';

export function AuthLoading() {
  return (
    <div className="page" role="status" aria-live="polite" style={{ textAlign: 'center', paddingTop: 100 }}>
      Loading...
    </div>
  );
}

/**
 * Guards a route (or a whole group of routes) that needs a signed-in user.
 *
 * The JWT lives in an HttpOnly cookie, so JavaScript cannot read it — and a token's mere presence says nothing
 * about whether it is still valid. Validity is therefore decided by the server: AuthProvider asks `/users/me` on
 * startup, re-checks when the tab regains focus, and drops the session on any 401. This guard only reads the
 * resulting `isAuth`, so an expired token ends up here the same way a missing one does.
 *
 *   <ProtectedRoute><Library /></ProtectedRoute>                       wrapper form
 *   <Route element={<ProtectedRoute roles={['admin']} />}> ... </Route>  layout-route form (renders <Outlet />)
 *
 * Unauthenticated users are sent to /login with the requested location in router state, so they land back on it
 * after logging in. Signed-in users without a required role go to the home page.
 */
export default function ProtectedRoute({ children, roles }) {
  const { isAuth, user, loading } = useAuth();
  const location = useLocation();

  if (loading) return <AuthLoading />;
  if (!isAuth) return <Navigate to="/login" replace state={{ from: location }} />;
  if (roles && !roles.includes(user.role)) return <Navigate to="/" replace />;
  return children ?? <Outlet />;
}

/** HOC form: `export default withAuth(Library)` or `withAuth(AdminPanel, { roles: ['admin'] })`. */
export function withAuth(Component, options = {}) {
  function WithAuth(props) {
    return (
      <ProtectedRoute {...options}>
        <Component {...props} />
      </ProtectedRoute>
    );
  }
  WithAuth.displayName = `withAuth(${Component.displayName || Component.name || 'Component'})`;
  return WithAuth;
}
