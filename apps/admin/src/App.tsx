import { DeveloperBadge } from './DeveloperBadge';

export function App() {
  return (
    <main
      style={{
        minHeight: '100vh',
        display: 'flex',
        flexDirection: 'column',
        justifyContent: 'space-between',
        padding: '2rem',
        boxSizing: 'border-box',
        fontFamily: 'system-ui, sans-serif',
        background: '#0d1117',
        color: '#c9d1d9',
      }}
    >
      <div>
        <h1 style={{ color: '#f0f6fc', margin: 0 }}>Bosun Admin</h1>
        <p style={{ color: '#8b949e', marginTop: '0.5rem' }}>The admin console is not built yet.</p>
      </div>

      <footer style={{ marginTop: '2rem', paddingTop: '1rem', borderTop: '1px solid #30363d' }}>
        <DeveloperBadge />
      </footer>
    </main>
  );
}
