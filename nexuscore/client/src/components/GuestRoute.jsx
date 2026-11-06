import { Navigate, useLocation } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { getRedirectTarget } from '../utils/authRedirect';
import { AuthLoading } from './ProtectedRoute';

/** For /login and /register: a user who is already signed in is sent on instead of seeing the form again. */
export default function GuestRoute({ children }) {
  const { isAuth, loading } = useAuth();
  const location = useLocation();

  if (loading) return <AuthLoading />;
  if (isAuth) return <Navigate to={getRedirectTarget(location)} replace />;
  return children;
}
