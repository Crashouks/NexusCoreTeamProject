import { useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { getRedirectTarget } from '../utils/authRedirect';
import { useAuth } from '../context/AuthContext';
import { useToast } from '../components/Toast';

export default function Register() {
  const [username, setUsername] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const { register } = useAuth();
  const { showToast } = useToast();
  const navigate = useNavigate();
  const location = useLocation();

  const handleSubmit = async (e) => {
    e.preventDefault();
    setLoading(true);
    try {
      await register(username, email, password);
      showToast('Account created!', 'success');
      navigate(getRedirectTarget(location), { replace: true });
    } catch (err) {
      showToast(err.message, 'error');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="page page--narrow">
      <h1 className="font-display gradient-text auth-title">Join NexusCore</h1>
      <p style={{ textAlign: 'center', color: 'var(--text-muted)', marginBottom: 32 }}>Create your account</p>
      <form onSubmit={handleSubmit} className="card" style={{ padding: 32 }}>
        {['Username', 'Email', 'Password'].map((label, i) => (
          <div key={label} style={{ marginBottom: 16 }}>
            <label style={{ fontSize: 13, color: 'var(--text-muted)', display: 'block', marginBottom: 6 }}>{label}</label>
            <input
              type={label === 'Password' ? 'password' : label === 'Email' ? 'email' : 'text'}
              value={[username, email, password][i]}
              onChange={e => [setUsername, setEmail, setPassword][i](e.target.value)}
              required
              style={{ width: '100%', padding: 10, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }}
            />
          </div>
        ))}
        <button type="submit" className="btn btn-primary" style={{ width: '100%', marginTop: 8 }} disabled={loading}>
          {loading ? 'Creating...' : 'Sign Up'}
        </button>
        <p style={{ textAlign: 'center', marginTop: 16, fontSize: 14, color: 'var(--text-muted)' }}>
          Have an account? <Link to="/login" style={{ color: 'var(--accent-glow)' }}>Log In</Link>
        </p>
      </form>
    </div>
  );
}
