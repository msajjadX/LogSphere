import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { toApiError } from '../api/client';
import { ErrorBanner, Spinner } from '../components/Feedback';

export function LoginPage() {
  const { user, login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const from = (location.state as { from?: string } | null)?.from ?? '/';

  if (user) return <Navigate to={from} replace />;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(username, password);
      navigate(from, { replace: true });
    } catch (err) {
      setError(toApiError(err).message);
      setBusy(false);
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-gray-50 px-4 dark:bg-gray-950">
      <div className="w-full max-w-sm">
        <div className="mb-6 flex flex-col items-center gap-2">
          <img
            src="/brand/logo-light.jpg"
            alt="LogSphere - capturing all logs of applications, total visibility"
            className="w-full max-w-sm rounded-xl dark:hidden"
          />
          <img
            src="/brand/logo-dark.jpg"
            alt="LogSphere - capturing all logs of applications, total visibility"
            className="hidden w-full max-w-sm rounded-xl dark:block"
          />
          <h1 className="sr-only">LogSphere</h1>
        </div>
        <form onSubmit={submit} className="card space-y-4 p-6">
          {error && <ErrorBanner error={error} />}
          <div>
            <label htmlFor="login-username" className="label">
              Username
            </label>
            <input
              id="login-username"
              className="input"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              autoComplete="username"
              autoFocus
              required
            />
          </div>
          <div>
            <label htmlFor="login-password" className="label">
              Password
            </label>
            <input
              id="login-password"
              type="password"
              className="input"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              required
            />
          </div>
          <button type="submit" className="btn-primary w-full" disabled={busy || !username || !password}>
            {busy && <Spinner className="h-4 w-4 !text-white" />}
            Sign in
          </button>
        </form>
      </div>
    </div>
  );
}
