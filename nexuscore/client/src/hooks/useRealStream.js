import { useEffect, useRef, useCallback, useState } from 'react';
import * as signalR from '@microsoft/signalr';

import { getApiBase } from '../api/api';

const HUB = '/hubs/cloud-stream';

const KEY_VK = {
  Backspace: 0x08, Tab: 0x09, Enter: 0x0D, Escape: 0x1B, Space: 0x20,
  ArrowLeft: 0x25, ArrowUp: 0x26, ArrowRight: 0x27, ArrowDown: 0x28,
  Delete: 0x2E,
  ShiftLeft: 0xA0, ShiftRight: 0xA1,
  ControlLeft: 0xA2, ControlRight: 0xA3,
  AltLeft: 0xA4, AltRight: 0xA5,
  MetaLeft: 0x5B, MetaRight: 0x5C,
  F1: 0x70, F2: 0x71, F3: 0x72, F4: 0x73, F5: 0x74, F6: 0x75,
  F7: 0x76, F8: 0x77, F9: 0x78, F10: 0x79, F11: 0x7A, F12: 0x7B,
};

const PING_INTERVAL_MS = 5000;
const RECONNECT_GIVE_UP_MS = 5 * 60 * 1000;
const RECONNECT_DELAYS_MS = [0, 1000, 2000, 5000, 10000];

/**
 * Keeps trying to reconnect (instant, 1s, 2s, 5s, then every 10s with a little jitter so many tabs do not
 * hammer the API in lockstep after a restart) for up to 5 minutes. SignalR's built-in default gives up after
 * four attempts (~40s), which is too short for an API restart or a Tailscale/Wi-Fi blip.
 */
export const reconnectPolicy = {
  nextRetryDelayInMilliseconds(ctx) {
    if (ctx.elapsedMilliseconds >= RECONNECT_GIVE_UP_MS) {
      console.error(`[cloud-stream] Giving up reconnecting after ${Math.round(ctx.elapsedMilliseconds / 1000)}s`);
      return null;
    }
    const base = RECONNECT_DELAYS_MS[Math.min(ctx.previousRetryCount, RECONNECT_DELAYS_MS.length - 1)];
    const delay = base === 0 ? 0 : base + Math.round(Math.random() * 500);
    console.warn(`[cloud-stream] Reconnect attempt #${ctx.previousRetryCount + 1} in ${delay}ms`,
      ctx.retryReason?.message || '');
    return delay;
  },
};

function getHubUrl() {
  const base = getApiBase().replace(/\/api\/?$/, '');
  return `${base}${HUB}`;
}

function resolveVk(e) {
  if (KEY_VK[e.code] != null) return KEY_VK[e.code];
  if (e.code?.startsWith('Key') && e.code.length === 4) return e.code.charCodeAt(3);
  if (e.code?.startsWith('Digit') && e.code.length === 6) return e.code.charCodeAt(5);
  if (e.key?.length === 1) return e.key.toUpperCase().charCodeAt(0);
  return null;
}

/** Map pointer position to stream pixels (handles letterboxing from object-fit: contain). */
function getStreamDisplayRect(containerRect, streamW, streamH) {
  const cw = containerRect.width;
  const ch = containerRect.height;
  if (!cw || !ch || !streamW || !streamH) {
    return {
      left: containerRect.left,
      top: containerRect.top,
      width: cw,
      height: ch,
    };
  }
  const contentAspect = streamW / streamH;
  const containerAspect = cw / ch;
  let dw;
  let dh;
  if (containerAspect > contentAspect) {
    dh = ch;
    dw = ch * contentAspect;
  } else {
    dw = cw;
    dh = cw / contentAspect;
  }
  return {
    left: containerRect.left + (cw - dw) / 2,
    top: containerRect.top + (ch - dh) / 2,
    width: dw,
    height: dh,
  };
}

function clientToStream(clientX, clientY, containerEl, streamW, streamH) {
  const containerRect = containerEl.getBoundingClientRect();
  const display = getStreamDisplayRect(containerRect, streamW, streamH);
  const nx = (clientX - display.left) / display.width;
  const ny = (clientY - display.top) / display.height;
  if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return null;
  return {
    x: Math.max(0, Math.min(streamW - 1, Math.round(nx * streamW))),
    y: Math.max(0, Math.min(streamH - 1, Math.round(ny * streamH))),
  };
}

export function useRealStream(session, enabled, onSessionEnded) {
  const connRef = useRef(null);
  const canvasRef = useRef(null);
  const imgRef = useRef(new Image());
  const [streamStatus, setStreamStatus] = useState('idle');
  const [streamError, setStreamError] = useState(null);
  const [telemetry, setTelemetry] = useState(null);
  const [clientLatencyMs, setClientLatencyMs] = useState(null);
  const [connectNonce, setConnectNonce] = useState(0);
  const [inputFocused, setInputFocused] = useState(false);
  const [pointerLocked, setPointerLocked] = useState(false);
  const inputFocusedRef = useRef(false);
  const lastMoveAt = useRef(0);
  const pointerStateRef = useRef({ virtX: 0, virtY: 0, ready: false });

  // Keep the latest callback without making it an effect dependency — a new function identity must not
  // tear down and re-open the hub connection.
  const onSessionEndedRef = useRef(onSessionEnded);
  useEffect(() => { onSessionEndedRef.current = onSessionEnded; }, [onSessionEnded]);

  /** Manual "Try again" after the automatic reconnect window has been exhausted. */
  const reconnect = useCallback(() => setConnectNonce((n) => n + 1), []);

  const setFocused = useCallback((value) => {
    inputFocusedRef.current = value;
    setInputFocused(value);
  }, []);

  useEffect(() => {
    if (!enabled || !session?.session_id) {
      setStreamStatus('idle');
      setStreamError(null);
      setTelemetry(null);
      setClientLatencyMs(null);
      setFocused(false);
      setPointerLocked(false);
      return;
    }

    const hubUrl = getHubUrl();
    setStreamStatus('connecting');
    setStreamError(null);

    const conn = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, { withCredentials: true })
      .withAutomaticReconnect(reconnectPolicy)
      .configureLogging(signalR.LogLevel.Warning)
      .build();
    let disposed = false;
    let pingTimer = null;

    connRef.current = conn;
    imgRef.current.onload = () => {
      const canvas = canvasRef.current;
      if (!canvas) return;
      const ctx = canvas.getContext('2d');
      canvas.width = imgRef.current.width;
      canvas.height = imgRef.current.height;
      ctx.drawImage(imgRef.current, 0, 0);
      setStreamStatus('streaming');
    };

    conn.on('ReceiveFrame', (frameBase64) => {
      imgRef.current.src = `data:image/jpeg;base64,${frameBase64}`;
    });

    conn.on('StreamReady', () => {
      setStreamStatus('ready');
      setStreamError(null);
    });

    conn.on('SessionEnded', () => {
      onSessionEndedRef.current?.('game_closed');
    });

    // Pushed by the API the moment the Windows agent (or the user / admin / time limit) changes the stream state.
    conn.on('SessionStatusChanged', (t) => {
      console.info('[cloud-stream] Status →', t.status, t.reason || '');
      setTelemetry((prev) => ({ ...prev, ...t }));
    });

    // Full snapshot: sent on (re)join, on every latency sample, and when the agent connects/disconnects.
    conn.on('SessionTelemetry', (t) => {
      setTelemetry((prev) => ({ ...prev, ...t }));
    });

    const probeLatency = async () => {
      if (conn.state !== signalR.HubConnectionState.Connected) return;
      const t0 = performance.now();
      try {
        await conn.invoke('Ping');
        setClientLatencyMs(Math.round(performance.now() - t0));
      } catch { /* a failed probe is already covered by the reconnect handlers */ }
    };

    const joinSession = async () => {
      await conn.invoke('PlayerJoin', session.session_id);
      probeLatency();
    };

    conn.onreconnecting((err) => {
      console.warn('[cloud-stream] Connection dropped — reconnecting', err?.message || '');
      setStreamStatus('reconnecting');
      setClientLatencyMs(null);
      setStreamError(err?.message || 'Reconnecting to stream…');
    });

    conn.onreconnected((connectionId) => {
      console.info('[cloud-stream] Reconnected', connectionId);
      setStreamStatus('connected');
      setStreamError(null);
      // Groups are per-connection, so the new connection must join the session again; the server answers
      // with the current telemetry snapshot, so the UI is correct immediately.
      joinSession().catch((err) => {
        console.error('[cloud-stream] Re-join after reconnect failed', err);
        setStreamStatus('error');
        setStreamError(err.message || 'Could not rejoin the stream after reconnecting');
      });
    });

    conn.onclose((err) => {
      if (disposed) return;
      console.error('[cloud-stream] Connection closed', err?.message || '');
      setStreamStatus('disconnected');
      setClientLatencyMs(null);
      setStreamError(err?.message || 'Stream connection closed');
    });

    conn.start()
      .then(joinSession)
      .then(() => {
        setStreamStatus('connected');
        console.info('[cloud-stream] Player joined session', session.session_id, hubUrl);
        pingTimer = setInterval(probeLatency, PING_INTERVAL_MS);
      })
      .catch((err) => {
        if (disposed) return;
        console.error('[cloud-stream] Connect failed', err);
        setStreamStatus('error');
        setStreamError(err.message || 'Could not connect to stream hub — is the API running?');
      });

    // The browser knows before SignalR does when the network is back; if the automatic window was exhausted
    // while offline, restart right away instead of waiting for a manual retry.
    const onOnline = () => {
      if (conn.state === signalR.HubConnectionState.Disconnected) reconnect();
    };
    window.addEventListener('online', onOnline);

    return () => {
      disposed = true;
      if (pingTimer) clearInterval(pingTimer);
      window.removeEventListener('online', onOnline);
      conn.stop().catch(() => {});
      connRef.current = null;
    };
  }, [enabled, session?.session_id, connectNonce, reconnect, setFocused]);

  const sendInput = useCallback((input) => {
    connRef.current?.invoke('SendInput', session.session_id, input).catch(() => {});
  }, [session?.session_id]);

  const bindViewport = useCallback((el) => {
    if (!el || !enabled) return () => {};

    const pointerState = pointerStateRef.current;
    const canvasSize = () => {
      const canvas = canvasRef.current;
      return { w: canvas?.width || 1920, h: canvas?.height || 1080 };
    };

    const coordsFromClient = (clientX, clientY) => {
      const { w, h } = canvasSize();
      return clientToStream(clientX, clientY, el, w, h);
    };

    const virtCoords = () => {
      const { w, h } = canvasSize();
      pointerState.virtX = Math.max(0, Math.min(w - 1, pointerState.virtX));
      pointerState.virtY = Math.max(0, Math.min(h - 1, pointerState.virtY));
      return { x: Math.round(pointerState.virtX), y: Math.round(pointerState.virtY) };
    };

    const sendMoveFromClient = (clientX, clientY) => {
      const pt = coordsFromClient(clientX, clientY);
      if (!pt) return;
      pointerState.virtX = pt.x;
      pointerState.virtY = pt.y;
      pointerState.ready = true;
      sendInput({ type: 'move', x: pt.x, y: pt.y });
    };

    const tryPointerLock = () => {
      if (document.pointerLockElement === el) return;
      el.requestPointerLock?.().catch(() => {});
    };

    const onPointerLockChange = () => {
      const locked = document.pointerLockElement === el;
      setPointerLocked(locked);
      el.classList.toggle('cloud-pointer-locked', locked);
      if (locked) {
        setFocused(true);
        if (!pointerState.ready) {
          const { w, h } = canvasSize();
          pointerState.virtX = w / 2;
          pointerState.virtY = h / 2;
          pointerState.ready = true;
          sendInput({ type: 'move', ...virtCoords() });
        }
      }
    };

    const onPointerLockError = () => {
      setPointerLocked(false);
      el.classList.remove('cloud-pointer-locked');
    };

    const onMove = (e) => {
      const now = Date.now();
      if (now - lastMoveAt.current < 16) return;
      lastMoveAt.current = now;

      if (document.pointerLockElement === el) {
        const { w, h } = canvasSize();
        if (!pointerState.ready) {
          pointerState.virtX = w / 2;
          pointerState.virtY = h / 2;
          pointerState.ready = true;
        }
        pointerState.virtX += e.movementX;
        pointerState.virtY += e.movementY;
        sendInput({ type: 'move', ...virtCoords() });
        return;
      }

      if (!inputFocusedRef.current) return;
      sendMoveFromClient(e.clientX, e.clientY);
    };

    const onDown = (e) => {
      if (e.button !== 0 && e.button !== 1 && e.button !== 2) return;
      e.preventDefault();
      e.stopPropagation();
      el.focus({ preventScroll: true });
      setFocused(true);
      el.classList.add('cloud-input-focused');

      const pt = coordsFromClient(e.clientX, e.clientY);
      if (pt) {
        pointerState.virtX = pt.x;
        pointerState.virtY = pt.y;
        pointerState.ready = true;
        sendInput({
          type: 'mousedown',
          x: pt.x,
          y: pt.y,
          button: e.button === 2 ? 'right' : e.button === 1 ? 'middle' : 'left',
        });
      }

      tryPointerLock();
    };

    const onUp = (e) => {
      const pt = document.pointerLockElement === el
        ? virtCoords()
        : coordsFromClient(e.clientX, e.clientY);
      if (!pt) return;
      sendInput({
        type: 'mouseup',
        x: pt.x,
        y: pt.y,
        button: e.button === 2 ? 'right' : e.button === 1 ? 'middle' : 'left',
      });
    };

    const onKey = (e, down) => {
      if (['Tab', 'F5'].includes(e.key)) return;
      if (e.key === 'F11') return;
      if (e.key === 'Escape') {
        if (document.pointerLockElement === el) document.exitPointerLock?.();
        setFocused(false);
        el.classList.remove('cloud-input-focused', 'cloud-pointer-locked');
        return;
      }
      if (!inputFocusedRef.current && document.activeElement !== el) return;
      e.preventDefault();
      const vk = resolveVk(e);
      if (!vk) return;
      sendInput({ type: down ? 'keydown' : 'keyup', code: e.code, key: e.key, vk });
    };

    const onBlur = () => {
      if (document.pointerLockElement === el) return;
      setFocused(false);
      el.classList.remove('cloud-input-focused');
    };

    const onContextMenu = (e) => e.preventDefault();
    const onKeyDown = (e) => onKey(e, true);
    const onKeyUp = (e) => onKey(e, false);

    el.addEventListener('mousemove', onMove);
    el.addEventListener('mousedown', onDown);
    el.addEventListener('mouseup', onUp);
    el.addEventListener('contextmenu', onContextMenu);
    el.addEventListener('keydown', onKeyDown);
    el.addEventListener('keyup', onKeyUp);
    el.addEventListener('blur', onBlur);
    document.addEventListener('mouseup', onUp);
    document.addEventListener('pointerlockchange', onPointerLockChange);
    document.addEventListener('pointerlockerror', onPointerLockError);

    el.tabIndex = 0;
    el.style.outline = 'none';

    return () => {
      el.removeEventListener('mousemove', onMove);
      el.removeEventListener('mousedown', onDown);
      el.removeEventListener('mouseup', onUp);
      el.removeEventListener('contextmenu', onContextMenu);
      el.removeEventListener('keydown', onKeyDown);
      el.removeEventListener('keyup', onKeyUp);
      el.removeEventListener('blur', onBlur);
      document.removeEventListener('mouseup', onUp);
      document.removeEventListener('pointerlockchange', onPointerLockChange);
      document.removeEventListener('pointerlockerror', onPointerLockError);
      el.classList.remove('cloud-input-focused', 'cloud-pointer-locked');
      setFocused(false);
      setPointerLocked(false);
      pointerState.ready = false;
      if (document.pointerLockElement === el) {
        document.exitPointerLock?.();
      }
    };
  }, [enabled, sendInput, setFocused]);

  return {
    canvasRef,
    bindViewport,
    streamStatus,
    streamError,
    inputFocused,
    pointerLocked,
    /** Latest server snapshot: { status: 'starting'|'running'|'stopped', reason, agent_online, agent_latency_ms, fps, quality } */
    telemetry,
    /** Browser ↔ API round-trip in ms (null while disconnected). */
    clientLatencyMs,
    reconnect,
  };
}
