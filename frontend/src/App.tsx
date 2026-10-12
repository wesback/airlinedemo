import { useEffect, useState } from 'react';

type ApiState = 'checking' | 'ready' | 'unavailable';

export default function App() {
  const [apiState, setApiState] = useState<ApiState>('checking');

  useEffect(() => {
    let active = true;
    fetch('/api/health')
      .then((response) => {
        if (!response.ok) throw new Error('API health check failed');
        return response.json();
      })
      .then(() => { if (active) setApiState('ready'); })
      .catch(() => { if (active) setApiState('unavailable'); });
    return () => { active = false; };
  }, []);

  const message = {
    checking: 'Checking service connection…',
    ready: 'Service connected',
    unavailable: 'Service unavailable. Try again later.',
  }[apiState];

  return (
    <main>
      <h1>Airline asset review</h1>
      <p>Reviewer workspace</p>
      <p role="status" aria-live="polite">{message}</p>
    </main>
  );
}
