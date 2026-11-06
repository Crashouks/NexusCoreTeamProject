import { useState, useEffect, useRef, useCallback } from 'react';
import { Link } from 'react-router-dom';
import Icon from './Icon';
import { useRealStream } from '../hooks/useRealStream';
import { useSpectatorStream } from '../hooks/useSpectatorStream';
import PrivateStreamGate from './PrivateStreamGate';

const CURSOR_HIDE_MS = 2800;
const LEFT_EDGE_PX = 56;

function useTelemetry(active, targetFps) {
  const [stats, setStats] = useState({
    latency: 14, jitter: 2, bitrate: 24.5, packetLoss: 0.0, fps: targetFps, gpu: 42,
  });

  useEffect(() => {
    setStats(s => ({ ...s, fps: targetFps }));
  }, [targetFps]);

  useEffect(() => {
    if (!active) return;
    const iv = setInterval(() => {
      const variance = targetFps >= 120 ? 4 : 2;
      setStats({
        latency: 11 + Math.floor(Math.random() * 8),
        jitter: 1 + Math.floor(Math.random() * 4),
        bitrate: 22 + Math.random() * 6,
        packetLoss: Math.random() > 0.92 ? 0.1 : 0,
        fps: targetFps - variance + Math.floor(Math.random() * (variance * 2 + 1)),
        gpu: 38 + Math.floor(Math.random() * 18),
      });
    }, 1800);
    return () => clearInterval(iv);
  }, [active, targetFps]);

  return stats;
}

const HOST_STATUS = {
  starting: { label: 'Starting', color: 'var(--warning)' },
  running: { label: 'Running', color: 'var(--neon)' },
  stopped: { label: 'Stopped', color: 'var(--danger)' },
};

const STOP_REASONS = {
  launch_failed: 'The game failed to launch on the host PC.',
  capture_unavailable: 'The host could not capture the game window.',
  game_closed: 'The game was closed on the host PC.',
  user_ended: 'The session was ended.',
  time_limit: 'The session time limit was reached.',
  admin_ended: 'The session was ended by an administrator.',
};

/** What the overlay says while the stream is not yet showing frames, driven by the host's real state. */
function describeOverlay(streamStatus, streamError, telemetry) {
  // Reconnecting also carries an error message in the hook, so it must be checked before the generic error.
  if (streamStatus === 'reconnecting') {
    return { title: 'Connection lost', detail: 'Reconnecting automatically…', note: streamError || undefined };
  }
  if (streamError) return { title: 'Stream error', detail: streamError, isError: true };
  if (streamStatus === 'connecting') return { title: 'Connecting…', detail: 'Connecting to cloud stream…' };
  if (telemetry?.status === 'stopped') {
    return { title: 'Stream stopped', detail: STOP_REASONS[telemetry.reason] || 'The host stopped streaming.', isError: true };
  }
  if (telemetry?.status === 'running') {
    return { title: 'Game running', detail: telemetry.agent_online === false
      ? 'Host connection lost — waiting for it to come back…'
      : 'Waiting for the first video frame…' };
  }
  if (telemetry?.status === 'starting' || streamStatus === 'connected' || streamStatus === 'ready') {
    return {
      title: 'Starting game on host…',
      detail: 'Waiting for the game window to load — stream starts when the game is ready',
      note: 'Only the game window will be shown (not your desktop)',
    };
  }
  return { title: 'Launching game on your PC…', detail: `Stream: ${streamStatus}` };
}

function StatChip({ label, value, accent }) {
  return (
    <div className="cloud-stat-chip">
      <span className="cloud-stat-label">{label}</span>
      <span className="cloud-stat-value" style={accent ? { color: accent } : undefined}>{value}</span>
    </div>
  );
}

function NetworkBars({ quality }) {
  const bars = quality === '4K' ? 5 : quality === '1080p' ? 4 : 3;
  return (
    <div className="cloud-net-bars" aria-hidden="true">
      {[1, 2, 3, 4, 5].map(i => (
        <span key={i} className={i <= bars ? 'active' : ''} style={{ height: 4 + i * 3 }} />
      ))}
    </div>
  );
}

export default function CloudPlayer({
  session,
  watchTarget,
  onStopWatching,
  viewerQueue,
  elapsed,
  formatElapsed,
  displayQuality,
  targetFps,
  planLabel,
  statsLocked,
  onToggleStatsLock,
  onEnd,
  onGameClosed,
}) {
  const containerRef = useRef(null);
  const lastMoveRef = useRef(Date.now());
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [cursorVisible, setCursorVisible] = useState(true);
  const [leftEdgeHover, setLeftEdgeHover] = useState(false);
  const [muted, setMuted] = useState(false);
  const stats = useTelemetry(!!session || !!watchTarget, targetFps);
  const isWatching = !!watchTarget && !session;
  const isReal = !!session?.is_real_stream || (isWatching && !!watchTarget?.is_real_stream);
  const handleStreamEnded = useCallback(() => {
    onGameClosed?.();
  }, [onGameClosed]);
  const {
    canvasRef, bindViewport, streamStatus, streamError, inputFocused, pointerLocked,
    telemetry: hostTelemetry, clientLatencyMs, reconnect,
  } = useRealStream(
    session,
    !!session?.is_real_stream,
    handleStreamEnded,
  );
  const {
    canvasRef: spectatorCanvasRef,
    streamStatus: spectatorStatus,
    watchState,
    canView: canViewSpectator,
    clearCanvas,
  } = useSpectatorStream(watchTarget, isWatching);
  const inputLayerRef = useRef(null);
  // Real numbers for real streams: browser↔API round-trip + agent↔API round-trip ≈ end-to-end control latency.
  const hostLatencyMs = hostTelemetry?.agent_online === false ? null : hostTelemetry?.agent_latency_ms ?? null;
  const realLatencyMs = clientLatencyMs != null && hostLatencyMs != null ? clientLatencyMs + hostLatencyMs : null;
  const hostStatus = HOST_STATUS[hostTelemetry?.status];
  const overlay = describeOverlay(streamStatus, streamError, hostTelemetry);
  const displayStreamStatus = isWatching ? spectatorStatus : streamStatus;
  const showPrivateGate = isWatching && !canViewSpectator;

  useEffect(() => {
    if (showPrivateGate) clearCanvas();
  }, [showPrivateGate, clearCanvas]);

  useEffect(() => {
    if (!isReal || streamStatus !== 'streaming' || !inputLayerRef.current) return;
    return bindViewport(inputLayerRef.current);
  }, [isReal, streamStatus, bindViewport, session?.session_id]);

  const active = !!session || isWatching;
  const showStatsPanel = !isWatching && (statsLocked || (leftEdgeHover && cursorVisible));
  const showBottomBar = !isWatching && (statsLocked || cursorVisible);

  const toggleFullscreen = useCallback(async () => {
    const el = containerRef.current;
    if (!el) return;
    try {
      if (!document.fullscreenElement) await el.requestFullscreen();
      else await document.exitFullscreen();
    } catch { /* unsupported */ }
  }, []);

  useEffect(() => {
    const onChange = () => setIsFullscreen(document.fullscreenElement === containerRef.current);
    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
  }, []);

  useEffect(() => {
    const onKey = (e) => {
      if (e.key === 'F11') {
        e.preventDefault();
        toggleFullscreen();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [toggleFullscreen]);

  useEffect(() => {
    if (!active) {
      setCursorVisible(true);
      setLeftEdgeHover(false);
      return undefined;
    }
    if (isReal && streamStatus === 'streaming') {
      setCursorVisible(!inputFocused || !pointerLocked);
      return undefined;
    }
    const markMove = () => {
      lastMoveRef.current = Date.now();
      setCursorVisible(true);
    };
    const iv = setInterval(() => {
      if (Date.now() - lastMoveRef.current > CURSOR_HIDE_MS) setCursorVisible(false);
    }, 400);
    window.addEventListener('mousemove', markMove);
    return () => {
      window.removeEventListener('mousemove', markMove);
      clearInterval(iv);
    };
  }, [active, isReal, streamStatus, inputFocused, pointerLocked]);

  const handlePlayerMouseMove = (e) => {
    if (!active || !containerRef.current) return;
    if (isReal && (inputFocused || streamStatus === 'streaming')) {
      if (!inputFocused) {
        lastMoveRef.current = Date.now();
      }
      return;
    }
    const rect = containerRef.current.getBoundingClientRect();
    const x = e.clientX - rect.left;
    setLeftEdgeHover(x <= LEFT_EDGE_PX);
    lastMoveRef.current = Date.now();
    setCursorVisible(true);
  };

  const handlePlayerMouseLeave = () => {
    if (!statsLocked) setLeftEdgeHover(false);
  };

  return (
    <div
      ref={containerRef}
      className={`cloud-player ${isFullscreen ? 'cloud-player-fs' : ''} ${active && !cursorVisible ? 'cloud-cursor-hidden' : ''} ${inputFocused ? 'cloud-input-active' : ''} ${pointerLocked ? 'cloud-pointer-locked' : ''}`}
      onMouseMove={handlePlayerMouseMove}
      onMouseLeave={handlePlayerMouseLeave}
    >
      <div className="cloud-player-frame">
        <div className="cloud-player-corners" aria-hidden="true">
          <span /><span /><span /><span />
        </div>

        {active ? (
          <div
            className={`cloud-player-viewport ${isReal && displayStreamStatus === 'streaming' ? 'cloud-stream-active' : ''} ${isWatching ? 'cloud-watch-mode' : ''}`}
            style={isReal || isWatching ? { background: '#000' } : { backgroundImage: `url(${session.cover_url})` }}
          >
            {isWatching && showPrivateGate && (
              <PrivateStreamGate
                watchState={watchState}
                viewerQueue={viewerQueue}
                onStopWatching={onStopWatching}
              />
            )}
            {isWatching && canViewSpectator && watchTarget?.is_real_stream && (
              <canvas ref={spectatorCanvasRef} className="cloud-real-canvas" />
            )}
            {isWatching && canViewSpectator && !watchTarget?.is_real_stream && (
              <>
                <div className="cloud-player-backdrop" />
                {watchTarget?.cover_url && (
                  <div className="cloud-watch-simulated" style={{ backgroundImage: `url(${watchTarget.cover_url})` }} />
                )}
                <div className="cloud-stream-status-overlay" style={{ pointerEvents: 'none' }}>
                  <strong style={{ color: 'var(--accent-glow)' }}>Spectator view</strong>
                  <span>{watchTarget?.game_name || 'Simulated stream'}</span>
                </div>
              </>
            )}
            {session && isReal && (
              <canvas ref={canvasRef} className="cloud-real-canvas" />
            )}
            {session && isReal && streamStatus === 'streaming' && (
              <div
                ref={inputLayerRef}
                className="cloud-input-layer"
                aria-label="Game stream input area"
              />
            )}
            {session && isReal && streamStatus !== 'streaming' && streamStatus !== 'idle' && (
              <div className="cloud-stream-status-overlay" style={{
                position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
                background: 'rgba(0,0,0,0.85)', color: 'var(--text-muted)', fontSize: 14, padding: 24, textAlign: 'center',
                flexDirection: 'column', gap: 8, pointerEvents: 'none',
              }}>
                <strong style={{ color: overlay.isError ? 'var(--danger)' : 'var(--accent-glow)' }}>
                  {overlay.title}
                </strong>
                <span>{overlay.detail}</span>
                {overlay.note && <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>{overlay.note}</span>}
                {overlay.isError && streamError && <span style={{ fontSize: 12 }}>Check Admin → Cloud → Connection log and nexuscore/agent/logs/agent.log</span>}
                {(streamStatus === 'disconnected' || streamStatus === 'error') && (
                  <button type="button" className="btn btn-neon" style={{ pointerEvents: 'auto', marginTop: 8 }} onClick={reconnect}>
                    Try again
                  </button>
                )}
              </div>
            )}
            {session && isReal && streamStatus === 'streaming' && (
              <div className="cloud-stream-focus-hint" style={{
                position: 'absolute', bottom: 12, left: '50%', transform: 'translateX(-50%)',
                background: 'rgba(0,0,0,0.55)', padding: '6px 12px', borderRadius: 8, fontSize: 12,
                color: 'var(--text-muted)', pointerEvents: 'none', opacity: inputFocused ? 0.35 : 0.9,
                transition: 'opacity 0.2s',
              }}>
                {inputFocused
                  ? (pointerLocked ? 'Esc to release mouse' : 'Keyboard active · Esc to release · move mouse over stream')
                  : 'Click stream to play — mouse and keyboard go to the game'}
              </div>
            )}
            {isWatching && canViewSpectator && (
              <div className="cloud-stream-focus-hint" style={{
                position: 'absolute', bottom: 12, left: '50%', transform: 'translateX(-50%)',
                background: 'rgba(0,0,0,0.55)', padding: '6px 12px', borderRadius: 8, fontSize: 12,
                color: 'var(--text-muted)', pointerEvents: 'none', opacity: 0.85,
              }}>
                Spectator mode — view only
              </div>
            )}
            {session && !isReal && <div className="cloud-player-backdrop" />}
            {session && !isReal && <div className="cloud-scanlines" aria-hidden="true" />}

            <div className={`cloud-left-edge-zone ${cursorVisible && !inputFocused ? '' : 'disabled'}`} aria-hidden="true" />

            {!inputFocused && !statsLocked && !isReal && (
              <div className="cloud-cursor-hidden-badge">Gameplay mode — move mouse to show cursor</div>
            )}

            <div className={`cloud-stats-drawer ${showStatsPanel ? 'open' : ''}`}>
              <div className="cloud-stats-drawer-head">
                <p className="cloud-panel-title">Stream Telemetry</p>
                <button
                  type="button"
                  className={`cloud-ctrl-btn ${statsLocked ? 'active' : ''}`}
                  onClick={onToggleStatsLock}
                  title={statsLocked ? 'Unlock HUD' : 'Lock HUD on screen'}
                >
                  <Icon name={statsLocked ? 'lock' : 'unlock'} size={14} />
                </button>
              </div>
              <div className="cloud-telemetry-grid">
                <StatChip
                  label="Latency"
                  value={isReal ? (realLatencyMs != null ? `${realLatencyMs} ms` : '—') : `${stats.latency} ms`}
                  accent="var(--neon)"
                />
                <StatChip label="Jitter" value={`${stats.jitter} ms`} />
                <StatChip label="Bitrate" value={`${stats.bitrate.toFixed(1)} Mbps`} />
                <StatChip label="Loss" value={`${stats.packetLoss.toFixed(1)}%`} accent={stats.packetLoss > 0 ? 'var(--danger)' : undefined} />
                <StatChip label="Render" value={`${stats.fps} FPS`} />
                <StatChip label="GPU Load" value={`${stats.gpu}%`} />
                {isReal && (
                  <>
                    <StatChip label="Host" value={hostStatus?.label || 'Connecting'} accent={hostStatus?.color} />
                    <StatChip
                      label="Host RTT"
                      value={hostLatencyMs != null ? `${hostLatencyMs} ms` : hostTelemetry?.agent_online === false ? 'Offline' : '—'}
                      accent={hostTelemetry?.agent_online === false ? 'var(--danger)' : undefined}
                    />
                  </>
                )}
              </div>
              <div className="cloud-waveform" aria-hidden="true">
                {Array.from({ length: 20 }, (_, i) => (
                  <span key={i} style={{ height: `${22 + Math.sin(i * 0.7 + elapsed * 0.15) * 18}%` }} />
                ))}
              </div>
              <div className="cloud-drawer-meta">
                <StatChip label="ENC" value="H.265" />
                <StatChip label="SERVER" value={session?.server_name || session?.server_region?.replace(/-/g, ' ') || 'EU-Central'} />
                <NetworkBars quality={displayQuality} />
              </div>
            </div>

            {showStatsPanel && session && (
              <div className="cloud-player-topbar compact">
                <span className="cloud-live-badge"><span className="live-dot" /> LIVE</span>
                <span className="cloud-game-title">{session.name}</span>
              </div>
            )}
            {isWatching && canViewSpectator && (
              <div className="cloud-player-topbar compact">
                <span className="cloud-live-badge"><span className="live-dot" /> WATCHING</span>
                <span className="cloud-game-title">{watchState?.game_name || watchTarget?.game_name || 'Live stream'}</span>
              </div>
            )}

            {session && !session.is_real_stream && (
              <div className={`cloud-player-center minimal ${showStatsPanel ? 'dimmed' : ''}`}>
                <img src={session.cover_url} alt="" className="cloud-poster" />
                <p className="font-display cloud-center-title">{session.name}</p>
                <div className="cloud-timer-row">
                  <span className="cloud-elapsed">{formatElapsed(elapsed)}</span>
                  {session.minutes_remaining != null && (
                    <span className="cloud-remaining">{session.minutes_remaining} min left</span>
                  )}
                </div>
              </div>
            )}

            {isWatching ? (
              <div className="cloud-player-bottombar visible">
                <div className="cloud-bottom-left">
                  <span className="cloud-quality-pill">Spectator</span>
                  <span className="cloud-hint">{showPrivateGate ? 'Private stream' : 'View only'}</span>
                </div>
                <div className="cloud-bottom-controls">
                  <button type="button" className="cloud-ctrl-btn" onClick={toggleFullscreen} title="Fullscreen (F11)">
                    <Icon name={isFullscreen ? 'minimize' : 'maximize'} size={16} />
                  </button>
                  <button type="button" className="btn btn-ghost cloud-end-btn" onClick={onStopWatching}>Stop Watching</button>
                </div>
              </div>
            ) : (
            <div className={`cloud-player-bottombar ${showBottomBar ? 'visible' : ''}`}>
              <div className="cloud-bottom-left">
                <span className="cloud-quality-pill">{displayQuality}</span>
                <span className="cloud-quality-pill">{targetFps} FPS</span>
                {!statsLocked && <span className="cloud-hint">← edge · F11</span>}
              </div>
              <div className="cloud-bottom-controls">
                <button type="button" className={`cloud-ctrl-btn ${statsLocked ? 'active' : ''}`} onClick={onToggleStatsLock} title="Lock stats HUD">
                  <Icon name={statsLocked ? 'lock' : 'unlock'} size={16} />
                </button>
                <button type="button" className="cloud-ctrl-btn" onClick={() => setMuted(m => !m)} title={muted ? 'Unmute' : 'Mute'}>
                  <Icon name={muted ? 'volumeOff' : 'volume'} size={16} />
                </button>
                <button type="button" className="cloud-ctrl-btn" onClick={toggleFullscreen} title="Fullscreen (F11)">
                  <Icon name={isFullscreen ? 'minimize' : 'maximize'} size={16} />
                </button>
                <button type="button" className="btn btn-danger cloud-end-btn" onClick={onEnd}>End Session</button>
              </div>
            </div>
            )}
          </div>
        ) : (
          <div className="cloud-player-viewport cloud-player-idle">
            <div className="cloud-idle-grid" aria-hidden="true" />
            <div className="cloud-idle-content">
              <div className="cloud-idle-rings">
                <Icon name="cloud" size={40} />
              </div>
              <p className="font-display cloud-idle-title">GeForce NOW Stream Deck</p>
              <p className="cloud-idle-sub">
                No active session · <strong>{planLabel}</strong> plan ready
              </p>
              <div className="cloud-idle-status-row">
                <StatChip label="Status" value="Standby" accent="var(--accent-glow)" />
                <StatChip label="Region" value="EU-Central" />
                <StatChip label="Queue" value={planLabel === 'Free' ? 'Enabled' : 'Bypass'} />
              </div>
              <p className="cloud-idle-hint">
                Launch a cloud-ready title from your library, then select <strong>Stream Now</strong>.
              </p>
              <div className="cloud-idle-actions">
                <Link to="/library?tab=cloud" className="btn btn-neon">Browse Cloud Games</Link>
                <Link to="/store?cloud=1" className="btn btn-ghost">Cloud Store</Link>
              </div>
            </div>
            <div className="cloud-player-bottombar cloud-idle-bar visible">
              <span className="cloud-hint">Press F11 for fullscreen stream view</span>
              <button type="button" className="cloud-ctrl-btn" onClick={toggleFullscreen} title="Fullscreen (F11)">
                <Icon name="maximize" size={16} />
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
