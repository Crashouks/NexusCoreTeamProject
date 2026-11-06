import { useState, useEffect, useMemo, useCallback } from 'react';
import { api } from '../api/api';
import { useToast } from '../components/Toast';
import Modal from '../components/Modal';
import ExePathInput from '../components/ExePathInput';
import Icon from '../components/Icon';
import { downloadAgentConfigFile, copyAgentSetup, getAgentApiUrl } from '../utils/agentConfig';

const TABS = ['Dashboard', 'Users', 'Games', 'Storefront', 'Moderation', 'Cloud', 'Trials', 'Forums', 'Purchases'];

const STAT_LABELS = {
  users: 'Total Users',
  games: 'Approved Games',
  pending_games: 'Pending Review',
  dev_requests: 'Dev Requests',
  active_cloud_sessions: 'Live Streams',
  queue_waiting: 'Queue Waiting',
  cloud_servers_online: 'Servers Online',
  cloud_server_slots: 'Stream Slots',
  active_trials: 'Active Trials',
  trials_today: 'Trials Today',
  total_revenue: 'Store Revenue',
  total_purchases: 'Total Purchases',
  wishlist_items: 'Wishlist Items',
  installed_games: 'Installed Games',
  forum_topics: 'Forum Topics',
  pending_friend_requests: 'Friend Requests',
};

function formatStat(key, value) {
  if (key === 'total_revenue') return `$${parseFloat(value || 0).toFixed(2)}`;
  return value ?? 0;
}

export default function AdminPanel() {
  const [tab, setTab] = useState(0);
  const [loading, setLoading] = useState(true);
  const [stats, setStats] = useState(null);
  const [users, setUsers] = useState([]);
  const [games, setGames] = useState([]);
  const [pending, setPending] = useState([]);
  const [devRequests, setDevRequests] = useState([]);
  const [cloudData, setCloudData] = useState({ sessions: [], queue: [] });
  const [cloudServers, setCloudServers] = useState({ servers: [], capacity: 0, active_sessions: 0, available_slots: 0 });
  const [cloudDiagnostics, setCloudDiagnostics] = useState({ logs: [], agent_log_file: '' });
  const [cloudSetupInfo, setCloudSetupInfo] = useState(null);
  const [editServer, setEditServer] = useState(null);
  const [manageGamesServer, setManageGamesServer] = useState(null);
  const [serverGameDraft, setServerGameDraft] = useState([]);
  const [gamesModalDraft, setGamesModalDraft] = useState([]);
  const [gamesModalSaving, setGamesModalSaving] = useState(false);
  const [trialData, setTrialData] = useState({ trials: [], stats: {} });
  const [purchases, setPurchases] = useState([]);
  const [forums, setForums] = useState([]);
  const [userSearch, setUserSearch] = useState('');
  const [gameFilter, setGameFilter] = useState('all');
  const [editGame, setEditGame] = useState(null);
  const [editUser, setEditUser] = useState(null);
  const [carouselDraft, setCarouselDraft] = useState([]);
  const [loadError, setLoadError] = useState(null);
  const [browseGameBusy, setBrowseGameBusy] = useState(false);
  const { showToast } = useToast();

  const approvedGames = useMemo(() => games.filter(g => g.status === 'approved'), [games]);
  const cloudEnabledGames = useMemo(() => approvedGames.filter(g => g.cloud_enabled), [approvedGames]);

  const buildServerGameDraft = useCallback((mappings = []) => {
    const byId = Object.fromEntries((mappings || []).map(m => [m.game_id, m.executable_path || '']));
    return cloudEnabledGames.map(g => ({
      game_id: g.game_id,
      name: g.name,
      enabled: Object.prototype.hasOwnProperty.call(byId, g.game_id),
      executable_path: byId[g.game_id] || '',
    }));
  }, [cloudEnabledGames]);

  const draftToMappings = useCallback((draft, isRealTier) => {
    return draft
      .filter(g => g.enabled)
      .map(g => ({
        game_id: g.game_id,
        executable_path: isRealTier ? g.executable_path.trim() : '',
      }))
      .filter(g => !isRealTier || g.executable_path);
  }, []);

  const loadServerGameDraft = useCallback(async (serverId) => {
    const data = await api.admin.cloudServerGames(serverId);
    return buildServerGameDraft(data.mappings || []);
  }, [buildServerGameDraft]);

  const saveServerGameMappings = useCallback(async (server, draft) => {
    const isRealTier = (server.server_tier || 'real') === 'real';
    const mappings = draftToMappings(draft, isRealTier);
    if (isRealTier) {
      const missing = draft.filter(g => g.enabled && !g.executable_path?.trim());
      if (missing.length) {
        throw new Error(`Set .exe path for: ${missing.map(g => g.name).join(', ')}`);
      }
      if (!mappings.length) {
        throw new Error('Select at least one game for a real PC server');
      }
    }
    await api.admin.setCloudServerGames(server.server_id, mappings);
    return mappings.length;
  }, [draftToMappings]);

  const applyExecutableToDraft = useCallback((setDraft, result) => {
    setDraft(prev => {
      const hit = prev.find(g => g.game_id === result.game_id);
      if (hit) {
        return prev.map(g => (g.game_id === result.game_id
          ? { ...g, enabled: true, executable_path: result.executable_path }
          : g));
      }
      return [...prev, {
        game_id: result.game_id,
        name: result.name,
        enabled: true,
        executable_path: result.executable_path,
      }];
    });
  }, []);

  useEffect(() => {
    setCarouselDraft(
      approvedGames
        .map(g => ({
          game_id: g.game_id,
          name: g.name,
          inCarousel: !!g.is_carousel,
          order: g.carousel_order ?? 0,
        }))
        .sort((a, b) => a.order - b.order)
    );
  }, [approvedGames]);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const results = await Promise.allSettled([
        api.admin.stats(),
        api.admin.users(),
        api.admin.games(),
        api.games.pending(),
        api.admin.devRequests(),
        api.cloud.sessionsAll(),
        api.admin.cloudServers(),
        api.trials.all(),
        api.admin.purchases(),
        api.admin.forums(),
      ]);
      const pick = (i, fallback) => results[i].status === 'fulfilled' ? results[i].value : fallback;
      const failed = results.filter(r => r.status === 'rejected');
      if (failed.length) {
        const msg = failed[0].reason?.message || 'Some admin data failed to load';
        setLoadError(msg);
        if (failed.some(r => r.reason?.status === 403)) {
          setLoadError('Admin access denied — log out and log in again, then restart the API.');
        }
      }
      setStats(pick(0, null));
      setUsers(pick(1, []));
      setGames(pick(2, []));
      setPending(pick(3, []));
      setDevRequests(pick(4, []));
      setCloudData(pick(5, { sessions: [], queue: [] }));
      setCloudServers(pick(6, { servers: [], capacity: 0, active_sessions: 0, available_slots: 0 }));
      setTrialData(pick(7, { trials: [], stats: {} }));
      setPurchases(pick(8, []));
      setForums(pick(9, []));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  const browseAndAddGame = useCallback(async (setDraft) => {
    setBrowseGameBusy(true);
    try {
      const pick = await api.admin.browseExecutable();
      if (pick.cancelled || !pick.path) return;
      const result = await api.admin.quickCloudGame({ executable_path: pick.path });
      if (result.created) await load();
      applyExecutableToDraft(setDraft, result);
      showToast(
        result.created
          ? `Added "${result.name}" to the store and this server`
          : `Linked "${result.name}" with ${pick.path}`,
        'success',
      );
    } catch (err) {
      showToast(err.message || 'Could not add game', 'error');
    } finally {
      setBrowseGameBusy(false);
    }
  }, [applyExecutableToDraft, load, showToast]);

  const loadCloudDiagnostics = useCallback(async () => {
    try {
      const data = await api.admin.cloudDiagnostics();
      setCloudDiagnostics(data);
    } catch { /* admin only */ }
  }, []);

  const loadCloudSetupInfo = useCallback(async () => {
    try {
      const data = await api.cloud.setupInfo();
      setCloudSetupInfo(data);
    } catch { /* ignore */ }
  }, []);

  useEffect(() => {
    if (tab !== 5) return;
    loadCloudDiagnostics();
    loadCloudSetupInfo();
    const iv = setInterval(() => {
      loadCloudDiagnostics();
      loadCloudSetupInfo();
    }, 5000);
    return () => clearInterval(iv);
  }, [tab, loadCloudDiagnostics, loadCloudSetupInfo]);

  const runAction = async (fn, successMsg) => {
    try {
      await fn();
      if (successMsg) showToast(successMsg, 'success');
      await load();
    } catch (err) {
      showToast(err.message || 'Action failed', 'error');
    }
  };

  const filteredUsers = users.filter(u =>
    !userSearch.trim() ||
    u.username?.toLowerCase().includes(userSearch.toLowerCase()) ||
    u.email?.toLowerCase().includes(userSearch.toLowerCase())
  );

  const filteredGames = games.filter(g => gameFilter === 'all' || g.status === gameFilter);

  const openNewServer = () => {
    setServerGameDraft(
      cloudEnabledGames.map(g => ({
        game_id: g.game_id,
        name: g.name,
        enabled: false,
        executable_path: '',
      }))
    );
    setEditServer({
      name: '', host: '', region: 'eu-central', gpu_model: 'RTX 4080',
      max_slots: 1, server_tier: 'real', access_password: '', player_password: '', status: 'offline', notes: '',
    });
  };

  const openEditServer = async (s) => {
    try {
      setServerGameDraft(await loadServerGameDraft(s.server_id));
      setEditServer({ ...s, access_password: '', player_password: '' });
    } catch (err) {
      showToast(err.message || 'Could not load game list', 'error');
    }
  };

  const openManageGames = async (s) => {
    try {
      setGamesModalDraft(await loadServerGameDraft(s.server_id));
      setManageGamesServer(s);
    } catch (err) {
      showToast(err.message || 'Could not load games for this server', 'error');
    }
  };
  const trialStats = trialData.stats || {};
  const isAgentLive = (s) => s.last_heartbeat && (Date.now() - new Date(s.last_heartbeat).getTime()) < 90000;
  const isRealServer = (s) => (s.server_tier || 'real') === 'real';

  const handleDownloadAgentConfig = (server, passwordOverride, playerPasswordOverride) => {
    const pwd = passwordOverride ?? '';
    const playerPwd = playerPasswordOverride ?? '';
    downloadAgentConfigFile(server.server_id, pwd, playerPwd);
    showToast('Downloaded config.json — save to nexuscore/agent/', 'success');
  };

  const handleCopyAgentSetup = async (server, passwordOverride) => {
    try {
      await copyAgentSetup(server.server_id, server.name, passwordOverride ?? '');
      showToast('Agent setup steps copied', 'success');
    } catch {
      showToast('Could not copy — use Download config instead', 'error');
    }
  };

  const convRate = trialStats.total > 0
    ? ((Number(trialStats.purchased || 0) / Number(trialStats.total)) * 100).toFixed(1)
    : 0;

  return (
    <div className="page">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 24, flexWrap: 'wrap', gap: 12 }}>
        <h1 className="font-display" style={{ fontSize: 32, color: 'var(--danger)', margin: 0 }}>Admin Panel</h1>
        <button type="button" className="btn btn-ghost" onClick={load} disabled={loading}>
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>

      {loadError && (
        <div className="card" style={{ padding: 16, marginBottom: 20, borderColor: 'var(--danger)', color: 'var(--danger)' }}>
          {loadError}
        </div>
      )}

      <div style={{ display: 'flex', gap: 4, marginBottom: 32, flexWrap: 'wrap', borderBottom: '1px solid var(--border)' }}>
        {TABS.map((t, i) => (
          <button key={t} type="button" onClick={() => setTab(i)} style={{
            padding: '10px 16px', background: 'none', border: 'none',
            borderBottom: tab === i ? '2px solid var(--danger)' : '2px solid transparent',
            color: tab === i ? 'var(--danger)' : 'var(--text-muted)', fontWeight: 600, fontSize: 14,
          }}>{t}</button>
        ))}
      </div>

      {loading && tab === 0 && !stats && (
        <div className="skeleton" style={{ height: 120 }} />
      )}

      {tab === 0 && stats && (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: 16 }}>
          {Object.entries(stats).map(([k, v]) => (
            <div key={k} className="glass" style={{ padding: 20, textAlign: 'center' }}>
              <p style={{ color: 'var(--text-muted)', fontSize: 12, marginBottom: 4 }}>
                {STAT_LABELS[k] || k.replace(/_/g, ' ')}
              </p>
              <p className="font-display" style={{ fontSize: 28, fontWeight: 700 }}>{formatStat(k, v)}</p>
            </div>
          ))}
        </div>
      )}

      {tab === 1 && !users.length && !loading && (
        <p style={{ color: 'var(--text-muted)', padding: 24, textAlign: 'center' }}>
          {loadError ? 'Could not load users.' : 'No users found.'}
        </p>
      )}

      {tab === 1 && !!users.length && (
        <>
          <input
            value={userSearch}
            onChange={e => setUserSearch(e.target.value)}
            placeholder="Search users…"
            style={{ width: '100%', maxWidth: 360, padding: 10, marginBottom: 16, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }}
          />
          <div className="card" style={{ overflow: 'auto' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                  {['ID', 'Username', 'Email', 'Role', 'Cloud', 'Balance', 'Library', 'Friends', 'Actions'].map(h => (
                    <th key={h} style={{ padding: '10px 12px', textAlign: 'left' }}>{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {filteredUsers.map(u => (
                  <tr key={u.user_id} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td style={{ padding: '10px 12px' }}>{u.user_id}</td>
                    <td style={{ padding: '10px 12px' }}>{u.username}</td>
                    <td style={{ padding: '10px 12px' }}>{u.email}</td>
                    <td style={{ padding: '10px 12px' }}>{u.role}</td>
                    <td style={{ padding: '10px 12px' }}>{u.cloud_plan}</td>
                    <td style={{ padding: '10px 12px' }}>${parseFloat(u.balance || 0).toFixed(2)}</td>
                    <td style={{ padding: '10px 12px' }}>{u.library_count ?? 0}</td>
                    <td style={{ padding: '10px 12px' }}>{u.friend_count ?? 0}</td>
                    <td style={{ padding: '10px 12px', display: 'flex', gap: 6 }}>
                      <button type="button" className="btn btn-ghost" style={{ padding: '4px 8px', fontSize: 12 }} onClick={() => setEditUser({ ...u, balanceDelta: '' })}>Edit</button>
                      {u.role !== 'admin' && (
                        <button type="button" className="btn btn-danger" style={{ padding: '4px 8px', fontSize: 12 }}
                          onClick={() => {
                            if (!confirm(`Delete user ${u.username}?`)) return;
                            runAction(() => api.admin.deleteUser(u.user_id), 'User deleted');
                          }}>Delete</button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      {tab === 2 && (
        <>
          <div style={{ display: 'flex', gap: 8, marginBottom: 16, flexWrap: 'wrap' }}>
            {['all', 'approved', 'pending', 'rejected'].map(f => (
              <button key={f} type="button" className={`btn ${gameFilter === f ? 'btn-primary' : 'btn-ghost'}`}
                style={{ padding: '6px 14px', fontSize: 13, textTransform: 'capitalize' }}
                onClick={() => setGameFilter(f)}>{f}</button>
            ))}
          </div>
          <div className="card" style={{ overflow: 'auto' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                  {['Name', 'Genre', 'Price', 'Owners', 'Wishlist', 'Cloud', 'Size GB', 'Status', 'Actions'].map(h => (
                    <th key={h} style={{ padding: '10px 12px', textAlign: 'left' }}>{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {filteredGames.map(g => (
                  <tr key={g.game_id} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td style={{ padding: '10px 12px' }}>{g.name}</td>
                    <td style={{ padding: '10px 12px' }}>{g.genre}</td>
                    <td style={{ padding: '10px 12px' }}>{g.is_free ? 'Free' : `$${g.price}`}</td>
                    <td style={{ padding: '10px 12px' }}>{g.owners_count ?? 0}</td>
                    <td style={{ padding: '10px 12px' }}>{g.wishlist_count ?? 0}</td>
                    <td style={{ padding: '10px 12px' }}>{g.cloud_enabled ? <Icon name="cloud" size={16} /> : '—'}</td>
                    <td style={{ padding: '10px 12px' }}>{g.download_size_gb ?? '—'}</td>
                    <td style={{ padding: '10px 12px' }}>{g.status}</td>
                    <td style={{ padding: '10px 12px', display: 'flex', gap: 6 }}>
                      <button type="button" className="btn btn-ghost" style={{ padding: '4px 8px', fontSize: 12 }} onClick={() => setEditGame({ ...g })}>Edit</button>
                      {g.status === 'pending' && (
                        <>
                          <button type="button" className="btn btn-success" style={{ padding: '4px 8px', fontSize: 12 }}
                            onClick={() => runAction(() => api.games.review(g.game_id, 'approve'), 'Approved')}>Approve</button>
                          <button type="button" className="btn btn-danger" style={{ padding: '4px 8px', fontSize: 12 }}
                            onClick={() => {
                              const reason = prompt('Rejection reason:');
                              if (!reason) return;
                              runAction(() => api.games.review(g.game_id, 'reject', reason), 'Rejected');
                            }}>Reject</button>
                        </>
                      )}
                      <button type="button" className="btn btn-danger" style={{ padding: '4px 8px', fontSize: 12 }}
                        onClick={() => {
                          if (!confirm('Delete this game?')) return;
                          runAction(() => api.games.delete(g.game_id), 'Game deleted');
                        }}>Delete</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      {tab === 3 && (
        <div style={{ display: 'grid', gap: 32 }}>
          <div className="glass" style={{ padding: 24 }}>
            <h3 className="font-display" style={{ marginBottom: 8 }}>Hero Carousel</h3>
            <p style={{ color: 'var(--text-muted)', fontSize: 14, marginBottom: 20 }}>
              Choose homepage hero slides and their order.
            </p>
            <div className="card" style={{ overflow: 'auto', marginBottom: 16 }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
                <thead>
                  <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                    {['In Carousel', 'Order', 'Game'].map(h => (
                      <th key={h} style={{ padding: '10px 12px', textAlign: 'left' }}>{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {carouselDraft.map(g => (
                    <tr key={g.game_id} style={{ borderBottom: '1px solid var(--border)' }}>
                      <td style={{ padding: '10px 12px' }}>
                        <input type="checkbox" checked={g.inCarousel}
                          onChange={e => setCarouselDraft(prev => prev.map(x => x.game_id === g.game_id
                            ? { ...x, inCarousel: e.target.checked, order: e.target.checked ? prev.filter(y => y.inCarousel).length : 0 }
                            : x))} />
                      </td>
                      <td style={{ padding: '10px 12px', width: 80 }}>
                        <input type="number" min="0" value={g.order} disabled={!g.inCarousel}
                          onChange={e => setCarouselDraft(prev => prev.map(x => x.game_id === g.game_id ? { ...x, order: parseInt(e.target.value, 10) || 0 } : x))}
                          style={{ width: 60, padding: 6, borderRadius: 6, background: 'var(--bg-card)', border: '1px solid var(--border)', color: 'var(--text)' }} />
                      </td>
                      <td style={{ padding: '10px 12px' }}>{g.name}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <button type="button" className="btn btn-primary" onClick={() => runAction(async () => {
              const items = carouselDraft.filter(g => g.inCarousel).sort((a, b) => a.order - b.order)
                .map((g, i) => ({ game_id: g.game_id, carousel_order: i }));
              await api.games.updateCarousel(items);
            }, 'Carousel updated')}>Save Carousel</button>
          </div>

          <div className="glass" style={{ padding: 24 }}>
            <h3 className="font-display" style={{ marginBottom: 8 }}>Store Discounts</h3>
            <div className="card" style={{ overflow: 'auto' }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
                <thead>
                  <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                    {['Game', 'Price', 'Discount %', 'Expires', 'Actions'].map(h => (
                      <th key={h} style={{ padding: '10px 12px', textAlign: 'left' }}>{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {approvedGames.filter(g => !g.is_free).map(g => (
                    <tr key={g.game_id} style={{ borderBottom: '1px solid var(--border)' }}>
                      <td style={{ padding: '10px 12px' }}>{g.name}</td>
                      <td style={{ padding: '10px 12px' }}>${parseFloat(g.price).toFixed(2)}</td>
                      <td style={{ padding: '10px 12px' }}>
                        <input type="number" min="0" max="90" defaultValue={g.discount_percent ?? ''} placeholder="0"
                          onChange={e => { g._discountDraft = e.target.value; }}
                          style={{ width: 70, padding: 6, borderRadius: 6, background: 'var(--bg-card)', border: '1px solid var(--border)', color: 'var(--text)' }} />
                      </td>
                      <td style={{ padding: '10px 12px' }}>
                        <input type="datetime-local" defaultValue={g.discount_expires_at ? String(g.discount_expires_at).slice(0, 16) : ''}
                          onChange={e => { g._expiresDraft = e.target.value || null; }}
                          style={{ padding: 6, borderRadius: 6, background: 'var(--bg-card)', border: '1px solid var(--border)', color: 'var(--text)' }} />
                      </td>
                      <td style={{ padding: '10px 12px' }}>
                        <button type="button" className="btn btn-ghost" style={{ padding: '4px 10px', fontSize: 12 }}
                          onClick={() => runAction(async () => {
                            const pct = g._discountDraft === '' ? null : parseInt(g._discountDraft, 10);
                            const expires = g._expiresDraft || null;
                            await api.games.update(g.game_id, { discount_percent: pct, discount_expires_at: expires });
                          }, `Discount saved for ${g.name}`)}>Save</button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      )}

      {tab === 4 && (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))', gap: 24 }}>
          <div>
            <h3 className="font-display" style={{ marginBottom: 16 }}>Pending Games ({pending.length})</h3>
            {pending.map(g => (
              <div key={g.game_id} className="card" style={{ padding: 16, marginBottom: 12 }}>
                <strong>{g.name}</strong>
                <p style={{ fontSize: 13, color: 'var(--text-muted)', marginTop: 4 }}>{g.genre} · {g.developer_name}</p>
                <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
                  <button type="button" className="btn btn-success" style={{ padding: '4px 12px', fontSize: 13 }}
                    onClick={() => runAction(() => api.games.review(g.game_id, 'approve'), 'Approved')}>Approve</button>
                  <button type="button" className="btn btn-danger" style={{ padding: '4px 12px', fontSize: 13 }}
                    onClick={() => {
                      const reason = prompt('Rejection reason:');
                      if (!reason) return;
                      runAction(() => api.games.review(g.game_id, 'reject', reason), 'Rejected');
                    }}>Reject</button>
                </div>
              </div>
            ))}
            {!pending.length && <p style={{ color: 'var(--text-muted)' }}>No pending games</p>}
          </div>
          <div>
            <h3 className="font-display" style={{ marginBottom: 16 }}>Developer Requests ({devRequests.length})</h3>
            {devRequests.map(d => (
              <div key={d.user_id} className="card" style={{ padding: 16, marginBottom: 12 }}>
                <strong>{d.username}</strong> — {d.developer_company}
                <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
                  <button type="button" className="btn btn-success" style={{ padding: '4px 12px', fontSize: 13 }}
                    onClick={() => runAction(() => api.admin.approveDev(d.user_id), 'Developer approved')}>Approve</button>
                  <button type="button" className="btn btn-danger" style={{ padding: '4px 12px', fontSize: 13 }}
                    onClick={() => runAction(() => api.admin.rejectDev(d.user_id), 'Developer rejected')}>Reject</button>
                </div>
              </div>
            ))}
            {!devRequests.length && <p style={{ color: 'var(--text-muted)' }}>No pending requests</p>}
          </div>
        </div>
      )}

      {tab === 5 && (
        <>
          <div className="glass" style={{ padding: 16, marginBottom: 20 }}>
            <h3 className="font-display" style={{ margin: '0 0 8px', fontSize: 18 }}>Remote / multi-device testing</h3>
            <p style={{ margin: '0 0 12px', fontSize: 13, color: 'var(--text-muted)' }}>
              Use <code style={{ fontSize: 11 }}>start-site-network.bat</code> on the host PC. Cloud agent and players use the URLs below (not localhost).
            </p>
            {cloudSetupInfo ? (
              <div style={{ fontSize: 13, lineHeight: 1.7 }}>
                <div><strong>Player website:</strong>{' '}
                  <code style={{ fontSize: 12 }}>{cloudSetupInfo.public_web_url || `(host IP):${cloudSetupInfo.web_port}`}</code>
                </div>
                <div><strong>Agent config apiUrl:</strong>{' '}
                  <code style={{ fontSize: 12 }}>{cloudSetupInfo.agent_config_api_url}</code>
                </div>
                {!cloudSetupInfo.public_api_url && (
                  <p style={{ margin: '10px 0 0', color: 'var(--warning)', fontSize: 12 }}>
                    Set <code>PUBLIC_API_URL</code> and <code>PUBLIC_WEB_URL</code> in <code>nexuscore/.env</code> for your Tailscale address, then restart with start-site-network.bat.
                  </p>
                )}
              </div>
            ) : (
              <p style={{ margin: 0, color: 'var(--text-muted)', fontSize: 12 }}>Loading setup info…</p>
            )}
          </div>

          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12, flexWrap: 'wrap', gap: 12 }}>
            <div>
              <h3 className="font-display" style={{ margin: 0 }}>Streaming Servers</h3>
              <p style={{ color: 'var(--text-muted)', fontSize: 13, margin: '6px 0 0' }}>
                Register your gaming PC, use <strong>Games</strong> to choose which titles each server provides, then run{' '}
                <code style={{ fontSize: 12 }}>start-cloud-gaming.bat</code>.
              </p>
            </div>
            <button type="button" className="btn btn-primary" style={{ fontSize: 13 }} onClick={openNewServer}>
              Add Machine
            </button>
          </div>

          <div className="glass" style={{ padding: 16, marginBottom: 24, display: 'flex', gap: 24, flexWrap: 'wrap' }}>
            <div><span style={{ color: 'var(--text-muted)', fontSize: 12 }}>Capacity</span><p style={{ margin: 4, fontWeight: 700 }}>{cloudServers.capacity ?? 0} slots</p></div>
            <div><span style={{ color: 'var(--text-muted)', fontSize: 12 }}>In use</span><p style={{ margin: 4, fontWeight: 700 }}>{cloudServers.active_sessions ?? 0}</p></div>
            <div><span style={{ color: 'var(--text-muted)', fontSize: 12 }}>Available</span><p style={{ margin: 4, fontWeight: 700, color: 'var(--neon)' }}>{cloudServers.available_slots ?? 0}</p></div>
            <div><span style={{ color: 'var(--text-muted)', fontSize: 12 }}>Registered</span><p style={{ margin: 4, fontWeight: 700 }}>{cloudServers.servers?.length ?? 0}</p></div>
          </div>

          <div className="card" style={{ overflow: 'auto', marginBottom: 32 }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                  {['Name', 'Host', 'Games', 'Slots', 'Password', 'Agent', 'Status', 'Actions'].map(h => (
                    <th key={h} style={{ padding: '10px 12px', textAlign: 'left' }}>{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {(cloudServers.servers || []).map(s => (
                  <tr key={s.server_id} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td style={{ padding: '10px 12px' }}>{s.name}</td>
                    <td style={{ padding: '10px 12px', fontFamily: 'monospace', fontSize: 12 }}>{s.host}</td>
                    <td style={{ padding: '10px 12px', maxWidth: 200 }}>
                      {s.game_count > 0 ? (
                        <span style={{ fontSize: 12 }} title={s.game_names || ''}>
                          {s.game_count} game{s.game_count !== 1 ? 's' : ''}
                          {s.game_names && (
                            <span style={{ display: 'block', color: 'var(--text-muted)', fontSize: 11, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                              {s.game_names}
                            </span>
                          )}
                        </span>
                      ) : (
                        <span style={{ color: 'var(--text-muted)', fontSize: 12 }}>
                          {(s.server_tier || 'real') === 'real' ? 'None mapped' : 'All cloud games'}
                        </span>
                      )}
                    </td>
                    <td style={{ padding: '10px 12px' }}>{s.active_sessions ?? 0} / {s.max_slots}</td>
                    <td style={{ padding: '10px 12px' }}>{s.has_password ? 'Yes' : 'None'}</td>
                    <td style={{ padding: '10px 12px', color: isAgentLive(s) ? 'var(--neon)' : 'var(--text-muted)' }}>
                      {isAgentLive(s) ? 'Connected' : 'Offline'}
                    </td>
                    <td style={{ padding: '10px 12px' }}>
                      <span style={{
                        color: s.status === 'online' ? 'var(--neon)' : s.status === 'maintenance' ? 'var(--warning)' : 'var(--text-muted)',
                        textTransform: 'capitalize',
                      }}>{s.status}</span>
                    </td>
                    <td style={{ padding: '10px 12px', display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                      <button type="button" className="btn btn-ghost" style={{ padding: '4px 8px', fontSize: 12 }}
                        onClick={() => openManageGames(s)}>Games</button>
                      <button type="button" className="btn btn-ghost" style={{ padding: '4px 8px', fontSize: 12 }}
                        onClick={() => openEditServer(s)}>Edit</button>
                      {isRealServer(s) && (
                        <button type="button" className="btn btn-primary" style={{ padding: '4px 8px', fontSize: 12 }}
                          title="Download config.json for nexuscore/agent"
                          onClick={() => handleDownloadAgentConfig(s)}>Agent</button>
                      )}
                      {s.status !== 'online' && (
                        <button type="button" className="btn btn-success" style={{ padding: '4px 8px', fontSize: 12 }}
                          onClick={() => runAction(() => api.admin.updateCloudServer(s.server_id, { status: 'online' }), `${s.name} is online`)}>Online</button>
                      )}
                      {s.status === 'online' && (
                        <button type="button" className="btn btn-ghost" style={{ padding: '4px 8px', fontSize: 12 }}
                          onClick={() => runAction(() => api.admin.updateCloudServer(s.server_id, { status: 'offline' }), `${s.name} is offline`)}>Offline</button>
                      )}
                      <button type="button" className="btn btn-danger" style={{ padding: '4px 8px', fontSize: 12 }}
                        onClick={() => {
                          if (!confirm(`Remove server "${s.name}"?`)) return;
                          runAction(() => api.admin.deleteCloudServer(s.server_id), 'Server removed');
                        }}>Delete</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!cloudServers.servers?.length && (
              <p style={{ color: 'var(--text-muted)', padding: 24, textAlign: 'center' }}>
                No machines yet. Add your PC, map game .exe paths, then run the cloud agent.
              </p>
            )}
          </div>

          <div style={{ marginBottom: 24 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 8, flexWrap: 'wrap', gap: 8 }}>
              <div>
                <h3 className="font-display" style={{ margin: 0, fontSize: 18 }}>Connection log</h3>
                <p style={{ margin: '4px 0 0', fontSize: 12, color: 'var(--text-muted)' }}>
                  API + stream hub events. Agent file: <code style={{ fontSize: 11 }}>{cloudDiagnostics.agent_log_file || 'nexuscore/agent/logs/agent.log'}</code>
                </p>
              </div>
              <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }} onClick={loadCloudDiagnostics}>Refresh</button>
            </div>
            <div className="card" style={{ maxHeight: 220, overflow: 'auto', fontFamily: 'monospace', fontSize: 11, padding: 12 }}>
              {(cloudDiagnostics.logs || []).length === 0 && (
                <p style={{ color: 'var(--text-muted)', margin: 0 }}>No events yet — try connecting agent or starting a stream.</p>
              )}
              {(cloudDiagnostics.logs || []).slice().reverse().map((entry, i) => (
                <div key={i} style={{
                  marginBottom: 6, paddingBottom: 6, borderBottom: '1px solid var(--border)',
                  color: entry.level === 'error' ? 'var(--danger)' : entry.level === 'warn' ? 'var(--warning)' : 'var(--text-muted)',
                }}>
                  <span style={{ color: 'var(--text-dim)' }}>{new Date(entry.at).toLocaleTimeString()}</span>
                  {' '}[{entry.level}] [{entry.source}] {entry.message}
                  {entry.detail && <span style={{ color: 'var(--text-dim)' }}> — {entry.detail}</span>}
                </div>
              ))}
            </div>
          </div>

          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
            <h3 className="font-display" style={{ margin: 0 }}>Active Cloud Sessions</h3>
            <button type="button" className="btn btn-danger" style={{ fontSize: 13 }}
              onClick={() => {
                if (!confirm('Clear entire cloud queue?')) return;
                runAction(() => api.admin.clearQueue(), 'Queue cleared');
              }}>Clear Queue ({cloudData.queue?.length || 0})</button>
          </div>
          <div className="card" style={{ overflow: 'auto', marginBottom: 32 }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                  {['User', 'Game', 'Plan', 'Server', 'Started', 'Status', 'Actions'].map(h => (
                    <th key={h} style={{ padding: '10px 12px', textAlign: 'left' }}>{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {(cloudData.sessions || []).filter(s => s.status === 'active').map(s => (
                  <tr key={s.session_id} style={{ borderBottom: '1px solid var(--border)', background: 'var(--neon-dim)' }}>
                    <td style={{ padding: '10px 12px' }}>{s.username}</td>
                    <td style={{ padding: '10px 12px' }}>{s.game_name}</td>
                    <td style={{ padding: '10px 12px' }}>{s.plan}</td>
                    <td style={{ padding: '10px 12px' }}>{s.server_name || s.server_region || '—'}</td>
                    <td style={{ padding: '10px 12px' }}>{new Date(s.started_at).toLocaleString()}</td>
                    <td style={{ padding: '10px 12px' }}>{s.status}</td>
                    <td style={{ padding: '10px 12px' }}>
                      <button type="button" className="btn btn-danger" style={{ padding: '4px 10px', fontSize: 12 }}
                        onClick={() => runAction(() => api.cloud.forceEnd(s.session_id), 'Session ended')}>Force End</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <h3 className="font-display" style={{ marginBottom: 16 }}>Queue</h3>
          <div className="card" style={{ overflow: 'auto' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                  {['User', 'Game', 'Status', 'Joined'].map(h => (
                    <th key={h} style={{ padding: '10px 12px', textAlign: 'left' }}>{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {(cloudData.queue || []).map(q => (
                  <tr key={q.queue_id} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td style={{ padding: '10px 12px' }}>{q.username}</td>
                    <td style={{ padding: '10px 12px' }}>{q.game_name}</td>
                    <td style={{ padding: '10px 12px' }}>{q.status}</td>
                    <td style={{ padding: '10px 12px' }}>{new Date(q.joined_at).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      {tab === 6 && (
        <>
          <div className="glass" style={{ padding: 20, marginBottom: 24, textAlign: 'center' }}>
            <p className="font-display" style={{ fontSize: 18 }}>
              {trialStats.total || 0} started → {trialStats.completed || 0} completed → {trialStats.purchased || 0} purchased ({convRate}%)
            </p>
          </div>
          <div className="card" style={{ overflow: 'auto' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                  {['User', 'Game', 'Started', 'Duration', 'Status'].map(h => (
                    <th key={h} style={{ padding: '10px 12px', textAlign: 'left' }}>{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {(trialData.trials || []).map(t => (
                  <tr key={t.trial_id} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td style={{ padding: '10px 12px' }}>{t.username}</td>
                    <td style={{ padding: '10px 12px' }}>{t.game_name}</td>
                    <td style={{ padding: '10px 12px' }}>{new Date(t.started_at).toLocaleString()}</td>
                    <td style={{ padding: '10px 12px' }}>{t.duration_mins} min</td>
                    <td style={{ padding: '10px 12px' }}>{t.status}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      {tab === 7 && (
        <div className="card" style={{ overflow: 'auto' }}>
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                {['Topic', 'Author', 'Posts', 'Created', 'Actions'].map(h => (
                  <th key={h} style={{ padding: '10px 12px', textAlign: 'left' }}>{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {forums.map(f => (
                <tr key={f.topic_id} style={{ borderBottom: '1px solid var(--border)' }}>
                  <td style={{ padding: '10px 12px' }}>{f.title}</td>
                  <td style={{ padding: '10px 12px' }}>{f.author}</td>
                  <td style={{ padding: '10px 12px' }}>{f.post_count}</td>
                  <td style={{ padding: '10px 12px' }}>{new Date(f.created_at).toLocaleString()}</td>
                  <td style={{ padding: '10px 12px' }}>
                    <button type="button" className="btn btn-danger" style={{ padding: '4px 10px', fontSize: 12 }}
                      onClick={() => {
                        if (!confirm(`Delete forum topic "${f.title}"?`)) return;
                        runAction(() => api.admin.deleteForumTopic(f.topic_id), 'Topic deleted');
                      }}>Delete</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {!forums.length && <p style={{ color: 'var(--text-muted)', padding: 24, textAlign: 'center' }}>No forum topics</p>}
        </div>
      )}

      {tab === 8 && (
        <div className="card" style={{ overflow: 'auto' }}>
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                {['User', 'Game', 'Price', 'Download', 'Date'].map(h => (
                  <th key={h} style={{ padding: '10px 12px', textAlign: 'left' }}>{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {purchases.map((p, i) => (
                <tr key={`${p.user_id}-${p.game_id}-${i}`} style={{ borderBottom: '1px solid var(--border)' }}>
                  <td style={{ padding: '10px 12px' }}>{p.username}</td>
                  <td style={{ padding: '10px 12px' }}>{p.game_name}</td>
                  <td style={{ padding: '10px 12px' }}>${parseFloat(p.purchase_price || 0).toFixed(2)}</td>
                  <td style={{ padding: '10px 12px' }}>{p.download_status || 'none'}</td>
                  <td style={{ padding: '10px 12px' }}>{new Date(p.purchase_date).toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {!purchases.length && <p style={{ color: 'var(--text-muted)', padding: 24, textAlign: 'center' }}>No purchases yet</p>}
        </div>
      )}

      <Modal
        open={!!editServer}
        onClose={() => setEditServer(null)}
        title={editServer?.server_id ? `Edit Machine — ${editServer.name}` : 'Add Gaming PC'}
        wide
        closeOnBackdrop={false}
        closeOnEscape={false}
        showCloseButton
      >
        {editServer && (
          <>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 12 }}>
              <div>
                <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Display name</label>
                <input value={editServer.name} onChange={e => setEditServer({ ...editServer, name: e.target.value })}
                  placeholder="My Gaming PC"
                  style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </div>
              <div>
                <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Host / IP (this PC)</label>
                <input value={editServer.host} onChange={e => setEditServer({ ...editServer, host: e.target.value })}
                  placeholder="192.168.1.50 or localhost"
                  style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </div>
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 12, marginBottom: 12 }}>
              <div>
                <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Region label</label>
                <input value={editServer.region || ''} onChange={e => setEditServer({ ...editServer, region: e.target.value })}
                  placeholder="home"
                  style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </div>
              <div>
                <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>GPU</label>
                <input value={editServer.gpu_model || ''} onChange={e => setEditServer({ ...editServer, gpu_model: e.target.value })}
                  placeholder="RTX 3060"
                  style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </div>
              <div>
                <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Max concurrent games</label>
                <input type="number" min="1" value={editServer.max_slots ?? 1}
                  onChange={e => setEditServer({ ...editServer, max_slots: parseInt(e.target.value, 10) || 1 })}
                  style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </div>
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 12 }}>
              <div>
                <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Server type</label>
                <select value={editServer.server_tier || 'real'} onChange={e => setEditServer({ ...editServer, server_tier: e.target.value })}
                  style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                  <option value="free_fake">Free (fake datacenter)</option>
                  <option value="paid_fake">Paid (fake datacenter)</option>
                  <option value="real">Private PC (real stream)</option>
                </select>
              </div>
              <div>
                <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Admin status</label>
                <select value={editServer.status || 'offline'} onChange={e => setEditServer({ ...editServer, status: e.target.value })}
                  style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                  <option value="offline">offline</option>
                  <option value="online">online</option>
                  <option value="maintenance">maintenance (password unlock)</option>
                </select>
              </div>
            </div>
            <div style={{ marginBottom: 12 }}>
              <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>
                Agent password (optional)
                {editServer.server_id && editServer.has_password ? ' — leave blank to keep' : ''}
              </label>
              <input type="password" value={editServer.access_password || ''} onChange={e => setEditServer({ ...editServer, access_password: e.target.value })}
                placeholder="For nexuscore/agent/config.json — saved into config on download"
                style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </div>
            <div style={{ marginBottom: 12 }}>
              <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>
                Player password (optional)
                {editServer.server_id && editServer.has_player_password ? ' — leave blank to keep' : ''}
              </label>
              <input type="password" value={editServer.player_password || ''} onChange={e => setEditServer({ ...editServer, player_password: e.target.value })}
                placeholder="Users enter this on the site to connect"
                style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              <p style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 6 }}>
                Player password is stored on the server for the website. It is also written into config.json as a reminder (the agent ignores it).
                {(editServer.server_tier || 'real') === 'real' && (
                  <> Saving this machine auto-downloads <strong>config.json</strong> with both passwords when you set them.</>
                )}
              </p>
            </div>
            {editServer.server_id && (
              <p style={{ fontSize: 12, color: 'var(--neon)', marginBottom: 12 }}>
                Agent server ID: <strong>{editServer.server_id}</strong>
                {' · '}
                API: <code style={{ fontSize: 11 }}>{getAgentApiUrl()}</code>
              </p>
            )}
            {editServer.server_id && (editServer.server_tier || 'real') === 'real' && (
              <div className="glass" style={{ padding: 14, marginBottom: 16 }}>
                <h4 style={{ margin: '0 0 8px', fontSize: 14 }}>Agent setup on this PC</h4>
                <ol style={{ margin: '0 0 12px', paddingLeft: 20, fontSize: 13, color: 'var(--text-muted)', lineHeight: 1.6 }}>
                  <li>Fill in <strong>Agent password</strong> (and player password if needed) above, then click <strong>Save</strong>.</li>
                  <li><strong>config.json</strong> downloads automatically — save it to <code style={{ fontSize: 11 }}>nexuscore/agent/config.json</code></li>
                  <li>Run <code style={{ fontSize: 11 }}>start-cloud-gaming.bat</code></li>
                  <li>Status here should show <strong style={{ color: 'var(--neon)' }}>Agent: Connected</strong></li>
                </ol>
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                  <button type="button" className="btn btn-primary" style={{ fontSize: 13 }}
                    onClick={() => handleDownloadAgentConfig(
                      { server_id: editServer.server_id, name: editServer.name },
                      editServer.access_password || '',
                      editServer.player_password || '',
                    )}>
                    Download config.json again
                  </button>
                  <button type="button" className="btn btn-ghost" style={{ fontSize: 13 }}
                    onClick={() => handleCopyAgentSetup(
                      { server_id: editServer.server_id, name: editServer.name },
                      editServer.access_password || '',
                    )}>
                    Copy setup steps
                  </button>
                </div>
              </div>
            )}
            <div style={{ marginBottom: 12 }}>
              <h4 style={{ margin: '0 0 8px', fontSize: 14 }}>Games this server provides</h4>
            <p style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 8 }}>
              Check which cloud games this server can stream.
              {(editServer.server_tier || 'real') === 'real'
                ? ' Use Browse to pick a .exe on this PC — no cover URL needed.'
                : ' Fake/datacenter servers only need checkboxes — no .exe path.'}
            </p>
            {(editServer.server_tier || 'real') === 'real' && (
              <button
                type="button"
                className="btn btn-neon btn-sm"
                style={{ marginBottom: 10 }}
                disabled={browseGameBusy}
                onClick={() => browseAndAddGame(setServerGameDraft)}
              >
                {browseGameBusy ? 'Opening file picker…' : 'Browse & add game (.exe)'}
              </button>
            )}
            <div className="card" style={{ overflow: 'auto', maxHeight: 260, marginBottom: 16 }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
                <thead>
                  <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                    <th style={{ padding: '8px 10px', textAlign: 'left', width: 44 }}>On</th>
                    <th style={{ padding: '8px 10px', textAlign: 'left' }}>Game</th>
                    {(editServer.server_tier || 'real') === 'real' && (
                      <th style={{ padding: '8px 10px', textAlign: 'left' }}>Executable path (.exe)</th>
                    )}
                  </tr>
                </thead>
                <tbody>
                  {serverGameDraft.map(g => (
                    <tr key={g.game_id} style={{ borderBottom: '1px solid var(--border)', opacity: g.enabled ? 1 : 0.65 }}>
                      <td style={{ padding: '8px 10px' }}>
                        <input type="checkbox" checked={!!g.enabled}
                          onChange={e => setServerGameDraft(prev => prev.map(x => x.game_id === g.game_id ? { ...x, enabled: e.target.checked } : x))} />
                      </td>
                      <td style={{ padding: '8px 10px', whiteSpace: 'nowrap' }}>{g.name}</td>
                      {(editServer.server_tier || 'real') === 'real' && (
                        <td style={{ padding: '8px 10px', minWidth: 220 }}>
                          <ExePathInput
                            value={g.executable_path}
                            disabled={!g.enabled}
                            onChange={path => setServerGameDraft(prev => prev.map(x => x.game_id === g.game_id ? { ...x, executable_path: path } : x))}
                          />
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
              {!serverGameDraft.length && (
                <p style={{ padding: 16, color: 'var(--text-muted)', textAlign: 'center' }}>No cloud-enabled games — enable Cloud on a game in the Games tab first.</p>
              )}
            </div>
            </div>
            <div style={{ marginBottom: 16 }}>
              <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Notes (optional)</label>
              <textarea value={editServer.notes || ''} onChange={e => setEditServer({ ...editServer, notes: e.target.value })}
                rows={2}
                style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)', resize: 'vertical' }} />
            </div>
            <button type="button" className="btn btn-primary" onClick={() => {
              const isRealTier = (editServer.server_tier || 'real') === 'real';
              runAction(async () => {
              const payload = {
                name: editServer.name,
                host: editServer.host,
                region: editServer.region,
                gpu_model: editServer.gpu_model,
                max_slots: parseInt(editServer.max_slots, 10) || 1,
                server_tier: editServer.server_tier || 'real',
                status: editServer.status,
                notes: editServer.notes || null,
              };
              if (editServer.access_password) payload.access_password = editServer.access_password;
              else if (!editServer.server_id) payload.access_password = '';
              if (editServer.player_password) payload.player_password = editServer.player_password;
              else if (!editServer.server_id) payload.player_password = '';

              const agentPwdForConfig = editServer.access_password || '';
              const playerPwdForConfig = editServer.player_password || '';
              const hadAgentPwd = !!editServer.has_password;

              let serverId = editServer.server_id;
              if (serverId) {
                await api.admin.updateCloudServer(serverId, payload);
              } else {
                payload.access_password = editServer.access_password || '';
                payload.player_password = editServer.player_password || '';
                const created = await api.admin.createCloudServer(payload);
                serverId = created.server_id;
              }
              const enabledGames = serverGameDraft.filter(g => g.enabled);
              if (enabledGames.length) {
                if (isRealTier) {
                  const missing = enabledGames.filter(g => !g.executable_path?.trim());
                  if (missing.length) {
                    throw new Error(`Set .exe path for: ${missing.map(g => g.name).join(', ')}`);
                  }
                }
                await api.admin.setCloudServerGames(serverId, draftToMappings(serverGameDraft, isRealTier));
              } else {
                await api.admin.setCloudServerGames(serverId, []);
              }

              if (isRealTier) {
                downloadAgentConfigFile(serverId, agentPwdForConfig, playerPwdForConfig);
                if (hadAgentPwd && !agentPwdForConfig) {
                  showToast('config.json downloaded — re-enter Agent password and save again to include it in the file', 'error');
                } else {
                  showToast('Saved — config.json downloaded. Copy to nexuscore/agent/ then run start-cloud-gaming.bat', 'success');
                }
              }

              setEditServer(null);
              }, isRealTier ? null : (editServer.server_id ? 'Machine updated' : 'Machine added'));
            }}>
              {editServer.server_id ? 'Save Changes' : 'Add Machine'}
            </button>
          </>
        )}
      </Modal>

      <Modal open={!!manageGamesServer} onClose={() => setManageGamesServer(null)} title={`Games — ${manageGamesServer?.name || 'Server'}`} wide>
        {manageGamesServer && (
          <>
            <p style={{ fontSize: 13, color: 'var(--text-muted)', marginBottom: 12 }}>
              Choose which cloud games <strong>{manageGamesServer.name}</strong> can provide when players pick a server.
              {isRealServer(manageGamesServer)
                ? ' Browse for .exe files on this PC or use Browse on each row.'
                : ' Datacenter/fake servers only need checkboxes.'}
            </p>
            {isRealServer(manageGamesServer) && (
              <button
                type="button"
                className="btn btn-neon btn-sm"
                style={{ marginBottom: 10 }}
                disabled={browseGameBusy}
                onClick={() => browseAndAddGame(setGamesModalDraft)}
              >
                {browseGameBusy ? 'Opening file picker…' : 'Browse & add game (.exe)'}
              </button>
            )}
            <div className="card" style={{ overflow: 'auto', maxHeight: 360, marginBottom: 16 }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
                <thead>
                  <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)' }}>
                    <th style={{ padding: '8px 10px', textAlign: 'left', width: 44 }}>Provide</th>
                    <th style={{ padding: '8px 10px', textAlign: 'left' }}>Game</th>
                    {isRealServer(manageGamesServer) && (
                      <th style={{ padding: '8px 10px', textAlign: 'left' }}>Executable path (.exe)</th>
                    )}
                  </tr>
                </thead>
                <tbody>
                  {gamesModalDraft.map(g => (
                    <tr key={g.game_id} style={{ borderBottom: '1px solid var(--border)', opacity: g.enabled ? 1 : 0.65 }}>
                      <td style={{ padding: '8px 10px' }}>
                        <input type="checkbox" checked={!!g.enabled}
                          onChange={e => setGamesModalDraft(prev => prev.map(x => x.game_id === g.game_id ? { ...x, enabled: e.target.checked } : x))} />
                      </td>
                      <td style={{ padding: '8px 10px' }}>{g.name}</td>
                      {isRealServer(manageGamesServer) && (
                        <td style={{ padding: '8px 10px', minWidth: 220 }}>
                          <ExePathInput
                            value={g.executable_path}
                            disabled={!g.enabled}
                            onChange={path => setGamesModalDraft(prev => prev.map(x => x.game_id === g.game_id ? { ...x, executable_path: path } : x))}
                          />
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
              {!gamesModalDraft.length && (
                <p style={{ padding: 16, color: 'var(--text-muted)', textAlign: 'center' }}>No cloud-enabled games — edit a game and check Cloud first.</p>
              )}
            </div>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <button type="button" className="btn btn-primary" disabled={gamesModalSaving}
                onClick={async () => {
                  setGamesModalSaving(true);
                  try {
                    const count = await saveServerGameMappings(manageGamesServer, gamesModalDraft);
                    showToast(
                      count
                        ? `Server now provides ${count} game${count !== 1 ? 's' : ''}`
                        : 'Cleared — this server offers all cloud games (default)',
                      'success',
                    );
                    setManageGamesServer(null);
                    await load();
                  } catch (err) {
                    showToast(err.message || 'Save failed', 'error');
                  } finally {
                    setGamesModalSaving(false);
                  }
                }}>
                {gamesModalSaving ? 'Saving…' : 'Save games'}
              </button>
              <button type="button" className="btn btn-ghost" onClick={() => setManageGamesServer(null)}>Cancel</button>
              <button type="button" className="btn btn-ghost" style={{ marginLeft: 'auto' }}
                onClick={() => setGamesModalDraft(prev => prev.map(g => ({ ...g, enabled: true })))}>
                Select all
              </button>
              <button type="button" className="btn btn-ghost"
                onClick={() => setGamesModalDraft(prev => prev.map(g => ({ ...g, enabled: false })))}>
                Clear all
              </button>
            </div>
          </>
        )}
      </Modal>

      <Modal open={!!editGame} onClose={() => setEditGame(null)} title="Edit Game" wide>
        {editGame && (
          <>
            {['name', 'genre', 'price', 'short_desc', 'download_size_gb'].map(f => (
              <div key={f} style={{ marginBottom: 12 }}>
                <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>{f.replace(/_/g, ' ')}</label>
                <input value={editGame[f] ?? ''} onChange={e => setEditGame({ ...editGame, [f]: e.target.value })}
                  style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </div>
            ))}
            <label style={{ display: 'flex', gap: 16, marginBottom: 16, flexWrap: 'wrap' }}>
              <label><input type="checkbox" checked={!!editGame.is_carousel} onChange={e => setEditGame({ ...editGame, is_carousel: e.target.checked })} /> Hero Carousel</label>
              <label><input type="checkbox" checked={!!editGame.cloud_enabled} onChange={e => setEditGame({ ...editGame, cloud_enabled: e.target.checked })} /> Cloud</label>
              <label><input type="checkbox" checked={!!editGame.is_featured} onChange={e => setEditGame({ ...editGame, is_featured: e.target.checked })} /> Featured</label>
              <label><input type="checkbox" checked={!!editGame.is_free} onChange={e => setEditGame({ ...editGame, is_free: e.target.checked })} /> Free</label>
            </label>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 16 }}>
              <div>
                <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Status</label>
                <select value={editGame.status || 'approved'} onChange={e => setEditGame({ ...editGame, status: e.target.value })}
                  style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                  <option value="approved">approved</option>
                  <option value="pending">pending</option>
                  <option value="rejected">rejected</option>
                </select>
              </div>
              <div>
                <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Carousel order</label>
                <input type="number" min="0" value={editGame.carousel_order ?? 0}
                  onChange={e => setEditGame({ ...editGame, carousel_order: parseInt(e.target.value, 10) || 0 })}
                  style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </div>
            </div>
            <button type="button" className="btn btn-primary" onClick={() => runAction(async () => {
              const payload = { ...editGame };
              if (payload.discount_expires_at === '') payload.discount_expires_at = null;
              if (payload.discount_percent === '' || payload.discount_percent == null) payload.discount_percent = null;
              else payload.discount_percent = parseInt(payload.discount_percent, 10) || null;
              await api.games.update(editGame.game_id, payload);
              setEditGame(null);
            }, 'Game updated')}>Save</button>
          </>
        )}
      </Modal>

      <Modal open={!!editUser} onClose={() => setEditUser(null)} title={`Edit User — ${editUser?.username}`}>
        {editUser && (
          <>
            <div style={{ marginBottom: 12 }}>
              <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Role</label>
              <select value={editUser.role} onChange={e => setEditUser({ ...editUser, role: e.target.value })}
                style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                <option value="user">user</option>
                <option value="developer">developer</option>
                <option value="admin">admin</option>
              </select>
            </div>
            <div style={{ marginBottom: 12 }}>
              <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Cloud plan</label>
              <select value={editUser.cloud_plan || 'free'} onChange={e => setEditUser({ ...editUser, cloud_plan: e.target.value })}
                style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                {['free', 'starter', 'pro', 'ultimate'].map(p => <option key={p} value={p}>{p}</option>)}
              </select>
            </div>
            <div style={{ marginBottom: 12 }}>
              <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Set balance ($)</label>
              <input type="number" step="0.01" value={editUser.balance ?? 0} onChange={e => setEditUser({ ...editUser, balance: e.target.value })}
                style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </div>
            <div style={{ marginBottom: 16 }}>
              <label style={{ fontSize: 13, color: 'var(--text-muted)' }}>Add funds ($)</label>
              <input type="number" step="0.01" placeholder="e.g. 50" value={editUser.balanceDelta}
                onChange={e => setEditUser({ ...editUser, balanceDelta: e.target.value })}
                style={{ width: '100%', padding: 8, marginTop: 4, borderRadius: 'var(--radius)', background: 'var(--bg-surface)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </div>
            <button type="button" className="btn btn-primary" onClick={() => runAction(async () => {
              const payload = { role: editUser.role, cloud_plan: editUser.cloud_plan };
              if (editUser.balance !== '' && editUser.balance != null) payload.balance = parseFloat(editUser.balance);
              if (editUser.balanceDelta) payload.balance_delta = parseFloat(editUser.balanceDelta);
              await api.admin.updateUser(editUser.user_id, payload);
              setEditUser(null);
            }, 'User updated')}>Save</button>
          </>
        )}
      </Modal>
    </div>
  );
}
