import { BrowserRouter, Routes, Route, Navigate, useLocation } from 'react-router-dom';
import { AuthProvider } from './context/AuthContext';
import { CloudProvider } from './context/CloudContext';
import { TrialProvider } from './context/TrialContext';
import { WishlistProvider } from './context/WishlistContext';
import { DownloadProvider } from './context/DownloadContext';
import { NotificationProvider } from './context/NotificationContext';
import { ToastProvider } from './components/Toast';
import Header from './components/Header';
import Footer from './components/Footer';
import CloudSessionBar from './components/CloudSessionBar';
import TrialBar from './components/TrialBar';
import ProtectedRoute from './components/ProtectedRoute';
import GuestRoute from './components/GuestRoute';
import ShopPage from './pages/ShopPage';
import Store from './pages/Store';
import GameDetail from './pages/GameDetail';
import Login from './pages/Login';
import Register from './pages/Register';
import Profile from './pages/Profile';
import Library from './pages/Library';
import Community from './pages/Community';
import CloudPage from './pages/CloudPage';
import Wishlist from './pages/Wishlist';
import Downloads from './pages/Downloads';
import DeveloperPanel from './pages/DeveloperPanel';
import AdminPanel from './pages/AdminPanel';
import AccountSettings from './pages/AccountSettings';
import PurchaseHistory from './pages/PurchaseHistory';
import RedeemKey from './pages/RedeemKey';
import NotFound from './pages/NotFound';
import BuyModal from './components/BuyModal';
import { useTrial } from './context/TrialContext';

function TrialExpiredModal() {
  const { expiredGame, setExpiredGame } = useTrial();
  return (
    <BuyModal
      open={!!expiredGame}
      onClose={() => setExpiredGame(null)}
      game={expiredGame ? { ...expiredGame, game_id: expiredGame.game_id } : null}
      trialExpired
    />
  );
}

/** /stream is the stream viewer's short URL; the viewer itself lives on the Cloud Play page. */
function StreamRedirect() {
  const { search, hash } = useLocation();
  return <Navigate to={`/cloud${search}${hash}`} replace />;
}

function AppContent() {
  return (
    <>
      <Header />
      <CloudSessionBar />
      <TrialBar />
      <TrialExpiredModal />
      <main className="app-main">
      <Routes>
        {/* Public */}
        <Route path="/" element={<ShopPage />} />
        <Route path="/store" element={<Store />} />
        <Route path="/games/:slug" element={<GameDetail />} />
        <Route path="/community" element={<Community />} />

        {/* Only for visitors — signed-in users are sent on to where they were headed (or home) */}
        <Route path="/login" element={<GuestRoute><Login /></GuestRoute>} />
        <Route path="/register" element={<GuestRoute><Register /></GuestRoute>} />

        {/* Requires a signed-in user; anyone else is redirected to /login and returned here afterwards */}
        <Route element={<ProtectedRoute />}>
          <Route path="/profile" element={<Profile />} />
          <Route path="/library" element={<Library />} />
          <Route path="/library/downloads" element={<Downloads />} />
          <Route path="/cloud" element={<CloudPage />} />
          <Route path="/stream" element={<StreamRedirect />} />
          <Route path="/wishlist" element={<Wishlist />} />
          <Route path="/settings" element={<AccountSettings />} />
          <Route path="/purchases" element={<PurchaseHistory />} />
          <Route path="/redeem" element={<RedeemKey />} />
        </Route>
        <Route element={<ProtectedRoute roles={['developer', 'admin']} />}>
          <Route path="/developer" element={<DeveloperPanel />} />
        </Route>
        <Route element={<ProtectedRoute roles={['admin']} />}>
          <Route path="/admin" element={<AdminPanel />} />
        </Route>

        {/* Legacy URLs */}
        <Route path="/friends" element={<Navigate to="/community?tab=friends" replace />} />
        <Route path="/community/forums" element={<Navigate to="/community?tab=forums" replace />} />
        <Route path="/community/friends" element={<Navigate to="/community?tab=friends" replace />} />
        <Route path="/community/reviews" element={<Navigate to="/community?tab=forums" replace />} />
        <Route path="/community/events" element={<Navigate to="/community?tab=forums" replace />} />
        <Route path="/cart" element={<Navigate to="/store" replace />} />

        {/* Anything else */}
        <Route path="*" element={<NotFound />} />
      </Routes>
      </main>
      <Footer />
    </>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <ToastProvider>
        <AuthProvider>
          <NotificationProvider>
            <WishlistProvider>
              <CloudProvider>
                <DownloadProvider>
                  <TrialProvider>
                    <AppContent />
                  </TrialProvider>
                </DownloadProvider>
              </CloudProvider>
            </WishlistProvider>
          </NotificationProvider>
        </AuthProvider>
      </ToastProvider>
    </BrowserRouter>
  );
}
