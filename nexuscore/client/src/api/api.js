/** API base for browser fetch — use /api on remote clients so Vite proxies to the backend. */
export function getApiBase() {
  let base = import.meta.env.VITE_API_URL || '/api';
  if (typeof window !== 'undefined' && /localhost|127\.0\.0\.1/.test(base)) {
    const h = window.location.hostname;
    if (h !== 'localhost' && h !== '127.0.0.1') base = '/api';
  }
  return base;
}

const API = getApiBase();

const FETCH_OPTS = { credentials: 'include' };

/** Fired on window when an authenticated API call is rejected with 401 (expired / invalid JWT). */
export const UNAUTHORIZED_EVENT = 'nc:unauthorized';

async function request(path, options = {}) {
  const headers = { 'Content-Type': 'application/json', ...options.headers };

  const res = await fetch(`${API}${path}`, { ...options, headers, ...FETCH_OPTS });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    const err = new Error(data.error || res.statusText || 'Request failed');
    err.code = data.code;
    err.status = res.status;
    // A 401 from /auth/* just means "wrong credentials"; anywhere else it means the session is gone.
    if (res.status === 401 && !path.startsWith('/auth/') && typeof window !== 'undefined') {
      window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));
    }
    throw err;
  }
  return data;
}

export const api = {
  get: (path) => request(path),
  post: (path, body) => request(path, {
    method: 'POST',
    body: body === undefined ? undefined : JSON.stringify(body),
  }),
  put: (path, body) => request(path, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (path) => request(path, { method: 'DELETE' }),

  auth: {
    login: (email, password) => api.post('/auth/login', { email, password }),
    register: (username, email, password) => api.post('/auth/register', { username, email, password }),
    logout: () => api.post('/auth/logout'),
  },
  users: {
    me: () => api.get('/users/me'),
    update: (id, data) => api.put(`/users/${id}`, data),
    library: (id) => api.get(`/users/${id}/library`),
    purchase: (id, gameId, applyDiscount = false) => api.post(`/users/${id}/library/${gameId}`, { applyDiscount }),
    topup: (id, amount) => api.post(`/users/${id}/topup`, { amount }),
    friends: (id) => api.get(`/users/${id}/friends`),
    friendRequests: (id) => api.get(`/users/${id}/friend-requests`),
    addFriend: (id, friendId) => api.post(`/users/${id}/friends/${friendId}`),
    acceptFriend: (id, friendId) => api.post(`/users/${id}/friends/${friendId}/accept`),
    rejectFriend: (id, friendId) => api.delete(`/users/${id}/friends/${friendId}/reject`),
    removeFriend: (id, friendId) => api.delete(`/users/${id}/friends/${friendId}`),
    countries: () => api.get('/users/countries'),
    search: (q) => api.get(`/users/search?q=${encodeURIComponent(q)}`),
    requestDeveloper: (company) => api.post('/users/request-developer', { company }),
  },
  games: {
    list: (params = '') => api.get(`/games${params ? '?' + params : ''}`),
    featured: () => api.get('/games/featured'),
    carousel: () => api.get('/games/carousel'),
    onSale: () => api.get('/games/on-sale'),
    updateCarousel: (items) => api.put('/games/carousel/manage', { items }),
    newReleases: () => api.get('/games/new-releases'),
    detail: (id) => api.get(`/games/${id}`),
    pending: () => api.get('/games/pending'),
    my: () => api.get('/games/my'),
    submit: (data) => api.post('/games', data),
    review: (id, action, reason) => api.post(`/games/${id}/review`, { action, reason }),
    userReview: (id, data) => api.post(`/games/${id}/user-review`, data),
    update: (id, data) => api.put(`/games/${id}`, data),
    delete: (id) => api.delete(`/games/${id}`),
  },
  notifications: {
    list: () => api.get('/notifications'),
    unreadCount: () => api.get('/notifications/unread-count'),
    markRead: (id) => api.post(`/notifications/${id}/read`),
    markAllRead: () => api.post('/notifications/read-all'),
  },
  wishlist: {
    list: () => api.get('/wishlist'),
    ids: () => api.get('/wishlist/ids'),
    add: (gameId) => api.post(`/wishlist/${gameId}`),
    remove: (gameId) => api.delete(`/wishlist/${gameId}`),
  },
  cloud: {
    plans: () => api.get('/cloud/plans'),
    servers: (gameId) => api.get(`/cloud/servers${gameId ? `?game_id=${gameId}` : ''}`),
    setupInfo: () => api.get('/cloud/setup-info'),
    subscribe: (plan) => api.post('/cloud/subscribe', { plan }),
    queueStatus: () => api.get('/cloud/queue/status'),
    queueJoin: (gameId) => api.post('/cloud/queue/join', { game_id: gameId }),
    queueLeave: () => api.post('/cloud/queue/leave'),
    sessionStart: (gameId, billingMode, serverId, serverPassword, allowSpectators) => api.post('/cloud/session/start', {
      game_id: gameId,
      billing_mode: billingMode,
      server_id: serverId,
      server_password: serverPassword || null,
      allow_spectators: !!allowSpectators,
    }),
    sessionEnd: () => api.post('/cloud/session/end'),
    sessionHeartbeat: () => api.post('/cloud/session/heartbeat'),
    sessionActive: () => api.get('/cloud/session/active'),
    updateSessionPrivacy: (sessionId, allowSpectators) => api.patch('/cloud/session/privacy', {
      session_id: sessionId,
      allow_spectators: !!allowSpectators,
    }),
    liveStreams: () => api.get('/cloud/streams/live'),
    sessionHistory: (page = 1) => api.get(`/cloud/session/history?page=${page}`),
    sessionsAll: () => api.get('/cloud/sessions/all'),
    forceEnd: (id) => api.post(`/cloud/sessions/${id}/force-end`),
  },
  trials: {
    status: (gameId) => api.get(`/trials/status/${gameId}`),
    start: (gameId) => api.post(`/trials/start/${gameId}`),
    end: (trialId) => api.post(`/trials/end/${trialId}`),
    heartbeat: (trialId) => api.post(`/trials/heartbeat/${trialId}`),
    history: () => api.get('/trials/history'),
    active: () => api.get('/trials/active'),
    all: () => api.get('/trials/all'),
  },
  media: {
    validateUrl: (url) => api.get(`/media/validate-url?url=${encodeURIComponent(url)}`),
    uploadAvatar: async (file) => {
      if (file.size > 10 * 1024 * 1024) throw new Error('Avatar must be 10 MB or smaller');
      const fd = new FormData();
      fd.append('file', file);
      const res = await fetch(`${API}/media/avatar`, {
        method: 'POST',
        body: fd,
        credentials: 'include',
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.error || 'Upload failed');
      return data;
    },
    upload: async (file, gameId) => {
      const fd = new FormData();
      fd.append('file', file);
      fd.append('game_id', String(gameId));
      const res = await fetch(`${API}/upload`, {
        method: 'POST',
        body: fd,
        credentials: 'include',
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data.error);
      return data;
    },
  },
  downloads: {
    list: () => api.get('/downloads'),
    start: (gameId) => api.post(`/downloads/${gameId}/start`),
    progress: (gameId, progress) => api.post(`/downloads/${gameId}/progress`, { progress }),
    complete: (gameId) => api.post(`/downloads/${gameId}/complete`),
  },
  forums: {
    list: () => api.get('/forums'),
    detail: (topicId) => api.get(`/forums/${topicId}`),
    create: (title) => api.post('/forums', { title }),
    post: (topicId, content) => api.post(`/forums/${topicId}/posts`, { content }),
  },
  chat: {
    list: () => api.get('/chat'),
    queueLobby: () => api.get('/chat/queue-lobby'),
    messages: (chatId, limit = 100) => api.get(`/chat/${chatId}/messages?limit=${limit}`),
    join: (chatId) => api.post(`/chat/${chatId}/join`),
    leave: (chatId) => api.post(`/chat/${chatId}/leave`),
    send: (chatId, text) => api.post(`/chat/${chatId}/messages`, { text }),
  },
  admin: {
    stats: () => api.get('/admin/stats'),
    users: () => api.get('/admin/users'),
    updateUser: (id, data) => api.put(`/admin/users/${id}`, data),
    deleteUser: (id) => api.delete(`/users/${id}`),
    games: () => api.get('/admin/games'),
    purchases: () => api.get('/admin/purchases'),
    forums: () => api.get('/admin/forums'),
    deleteForumTopic: (id) => api.delete(`/admin/forums/${id}`),
    clearQueue: () => api.post('/admin/cloud/clear-queue'),
    cloudServers: () => api.get('/admin/cloud/servers'),
    cloudDiagnostics: () => api.get('/admin/cloud/diagnostics'),
    cloudServerGames: (id) => api.get(`/admin/cloud/servers/${id}/games`),
    setCloudServerGames: (id, mappings) => api.put(`/admin/cloud/servers/${id}/games`, { mappings }),
    createCloudServer: (data) => api.post('/admin/cloud/servers', data),
    updateCloudServer: (id, data) => api.put(`/admin/cloud/servers/${id}`, data),
    deleteCloudServer: (id) => api.delete(`/admin/cloud/servers/${id}`),
    devRequests: () => api.get('/users/developer-requests'),
    approveDev: (id) => api.post(`/users/approve-developer/${id}`),
    rejectDev: (id) => api.post(`/users/reject-developer/${id}`),
    browseExecutable: () => api.post('/admin/browse-executable'),
    quickCloudGame: (data) => api.post('/admin/games/quick-cloud', data),
  },
};
