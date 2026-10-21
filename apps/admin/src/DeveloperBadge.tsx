import { useState } from 'react';

export function DeveloperBadge() {
  const [imgError, setImgError] = useState(false);
  const avatarUrl = 'https://avatars.githubusercontent.com/u/70420596?v=4';
  const githubUrl = 'https://github.com/TheWilsonGlobal';

  return (
    <div
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: '8px',
        padding: '6px 12px',
        borderRadius: '8px',
        background: '#151b23',
        border: '1px solid #30363d',
        fontSize: '12px',
        fontFamily: 'ui-monospace, monospace',
        color: '#8b949e',
      }}
    >
      <span>Engineered by</span>
      <a
        href={githubUrl}
        target="_blank"
        rel="noopener noreferrer"
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '6px',
          color: '#58a6ff',
          textDecoration: 'none',
          fontWeight: 600,
        }}
        title="Wilson Pham (@TheWilsonGlobal)"
      >
        <span
          style={{
            position: 'relative',
            width: '20px',
            height: '20px',
            borderRadius: '50%',
            overflow: 'hidden',
            border: '1px solid #58a6ff',
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            background: '#0d1117',
            flexShrink: 0,
            verticalAlign: 'middle',
          }}
        >
          {!imgError ? (
            <img
              src={avatarUrl}
              alt="Wilson Pham"
              style={{ width: '100%', height: '100%', objectFit: 'cover' }}
              referrerPolicy="no-referrer"
              onError={() => setImgError(true)}
            />
          ) : (
            <span style={{ fontSize: '8px', color: '#58a6ff', fontWeight: 'bold' }}>WP</span>
          )}
        </span>
        <span>TheWilsonGlobal (Wilson Pham)</span>
        <svg
          width="12"
          height="12"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2.5"
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden="true"
        >
          <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
          <polyline points="15 3 21 3 21 9" />
          <line x1="10" y1="14" x2="21" y2="3" />
        </svg>
      </a>
    </div>
  );
}
