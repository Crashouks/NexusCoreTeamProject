import { useState, useEffect } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/api';
import { useAuth } from '../context/AuthContext';
import GameCard from '../components/GameCard';
import BuyModal from '../components/BuyModal';
import LibraryDownloadList from '../components/LibraryDownloadList';
import { isInstalled, isDownloading } from '../utils/download';

export default function Library() {
  const [params] = useSearchParams();
  const tab = params.get('tab') || 'all';
  const [trials, setTrials] = useState([]);
  const [trialMap, setTrialMap] = useState({});
  const [buyGame, setBuyGame] = useState(null);
  const { library } = useAuth();

  useEffect(() => {
    api.trials.history().then(h => {
      setTrials(h.filter(t => t.status === 'completed'));
      const m = {}; h.forEach(t => { m[t.game_id] = t; }); setTrialMap(m);
    });
  }, []);

  const cloudGames = library.filter(g => g.cloud_enabled);
  const recentGames = [...library].sort((a, b) => new Date(b.last_played || b.purchase_date) - new Date(a.last_played || a.purchase_date));
  const downloadGames = library.filter(g => isDownloading(g) || !isInstalled(g));
  const installedGames = library.filter(g => isInstalled(g));

  const tabs = [
    { id: 'all', label: 'All Games', games: library },
    { id: 'recent', label: 'Recently Played', games: recentGames },
    { id: 'downloads', label: 'Downloads', games: downloadGames, isDownloads: true },
    { id: 'installed', label: 'Installed', games: installedGames },
    { id: 'cloud', label: 'Cloud Ready', games: cloudGames },
    { id: 'tried', label: 'Tried Games', games: trials },
  ];
  const current = tabs.find(t => t.id === tab) || tabs[0];
  const isTried = tab === 'tried';
  const isDownloads = current.isDownloads;

  return (
    <div className="page">
      <h1 className="font-display" style={{ fontSize: 32, marginBottom: 24 }}>My Library</h1>
      <div style={{ display: 'flex', gap: 8, marginBottom: 32, borderBottom: '1px solid var(--border)', flexWrap: 'wrap' }}>
        {tabs.map(t => (
          <Link key={t.id} to={`/library${t.id === 'all' ? '' : `?tab=${t.id}`}`} style={{
            padding: '10px 20px', borderBottom: tab === t.id ? '2px solid var(--accent)' : '2px solid transparent',
            color: tab === t.id ? 'var(--accent)' : 'var(--text-muted)', fontWeight: 600, fontSize: 14,
          }}>{t.label} ({t.games.length})</Link>
        ))}
      </div>

      {isDownloads ? (
        <>
          <p style={{ color: 'var(--text-muted)', marginBottom: 20, fontSize: 14 }}>
            Download games to play locally. Cloud-ready titles can be streamed without installing.
            Download time: ~1 second per 10 GB (100 GB ≈ 10 seconds).
          </p>
          <LibraryDownloadList games={library} />
        </>
      ) : current.games.length === 0 ? (
        <p style={{ color: 'var(--text-muted)', textAlign: 'center', padding: 60 }}>
          {isTried ? 'No completed trials.' : tab === 'installed' ? 'No installed games yet — check Downloads.' : 'Your library is empty.'}
        </p>
      ) : (
        <div className="game-grid">
          {current.games.map(g => {
            const trial = trialMap[g.game_id];
            return (
              <div key={g.game_id}>
                <GameCard game={g} trialStatus={trial?.status} />
                {!isTried && trial && trial.status === 'active' && (
                  <div style={{ padding: '8px 4px' }}>
                    <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>
                      <span>Trial progress</span>
                      <span>{trial.progressPercent || 0}%</span>
                    </div>
                    <div className="progress-bar"><div className="progress-bar-fill" style={{ width: `${trial.progressPercent || 0}%` }} /></div>
                  </div>
                )}
                {isTried && (
                  <button className="btn btn-primary" style={{ width: '100%', marginTop: 8, fontSize: 13 }} onClick={() => setBuyGame(g)}>
                    Buy Now — ${parseFloat(g.price).toFixed(2)}
                  </button>
                )}
              </div>
            );
          })}
        </div>
      )}
      <BuyModal open={!!buyGame} onClose={() => setBuyGame(null)} game={buyGame} trialExpired />
    </div>
  );
}
