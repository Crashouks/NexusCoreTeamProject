import { useEffect } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';

/** Rendered by the catch-all route for any URL that matches nothing else. */
export default function NotFound() {
  const { pathname } = useLocation();
  const { isAuth } = useAuth();

  useEffect(() => {
    const previous = document.title;
    document.title = 'Page not found — NexusCore';
    return () => { document.title = previous; };
  }, []);

  return (
    <div className="page" style={{ maxWidth: 600, margin: '60px auto', textAlign: 'center' }}>
      <div className="card" style={{ padding: 48 }}>
        <p className="font-display gradient-text" style={{ fontSize: 96, lineHeight: 1, margin: 0 }} aria-hidden="true">404</p>
        <h1 className="font-display" style={{ fontSize: 28, margin: '12px 0' }}>Page not found</h1>
        <p style={{ color: 'var(--text-muted)', marginBottom: 28, lineHeight: 1.6, overflowWrap: 'anywhere' }}>
          We couldn&apos;t find <code>{pathname}</code>. It may have been moved, or the link is mistyped.
        </p>
        <div style={{ display: 'flex', gap: 12, justifyContent: 'center', flexWrap: 'wrap' }}>
          <Link to="/" className="btn btn-primary">Back to Home</Link>
          <Link to="/store" className="btn btn-ghost">Browse the Store</Link>
          {isAuth
            ? <Link to="/library" className="btn btn-ghost">My Library</Link>
            : <Link to="/login" className="btn btn-ghost">Log In</Link>}
        </div>
      </div>
    </div>
  );
}
