import { useState } from 'react';
import { ExternalLink } from 'lucide-react';

interface DeveloperBadgeProps {
  variant?: 'compact' | 'sidebar';
}

export const DeveloperBadge: React.FC<DeveloperBadgeProps> = ({ variant = 'compact' }) => {
  const [imgError, setImgError] = useState(false);
  const avatarUrl = 'https://avatars.githubusercontent.com/u/70420596?v=4';
  const githubUrl = 'https://github.com/TheWilsonGlobal';

  if (variant === 'compact') {
    return (
      <a
        href={githubUrl}
        target="_blank"
        rel="noopener noreferrer"
        className="group flex items-center gap-2 px-2.5 py-1 rounded-md bg-muted/50 hover:bg-muted border border-border transition-colors text-xs font-mono text-muted-foreground hover:text-foreground"
        title="Engineered by TheWilsonGlobal (Wilson Pham)"
      >
        <span className="text-[10px] hidden sm:inline text-muted-foreground">Engineered by</span>
        <div className="relative w-5 h-5 rounded-full overflow-hidden border border-primary/50 shrink-0 bg-background flex items-center justify-center">
          {!imgError ? (
            <img
              src={avatarUrl}
              alt="Wilson Pham (@TheWilsonGlobal)"
              className="w-full h-full object-cover"
              referrerPolicy="no-referrer"
              onError={() => setImgError(true)}
            />
          ) : (
            <span className="font-bold text-[9px] text-primary">WP</span>
          )}
          <span className="absolute -bottom-0.5 -right-0.5 w-1.5 h-1.5 rounded-full bg-emerald-500 border border-background" />
        </div>
        <span className="font-semibold text-foreground group-hover:text-primary transition-colors flex items-center gap-0.5 text-xs">
          <span>TheWilsonGlobal</span>
          <ExternalLink className="w-2.5 h-2.5 opacity-60 group-hover:opacity-100" />
        </span>
      </a>
    );
  }

  // Sidebar variant
  return (
    <div className="rounded-lg bg-card/60 border border-border p-2.5 space-y-2 text-xs">
      <div className="flex items-center text-[10px] font-mono">
        <span className="text-primary font-semibold tracking-wider">ENGINEERED BY</span>
      </div>

      <a
        href={githubUrl}
        target="_blank"
        rel="noopener noreferrer"
        className="group flex items-center gap-2 text-foreground hover:text-primary transition-colors"
        title="Wilson Pham (@TheWilsonGlobal)"
      >
        <div className="relative w-6 h-6 rounded-full overflow-hidden border border-primary/60 shrink-0 group-hover:scale-105 transition-transform">
          {!imgError ? (
            <img
              src={avatarUrl}
              alt="Wilson Pham"
              className="w-full h-full object-cover"
              referrerPolicy="no-referrer"
              onError={() => setImgError(true)}
            />
          ) : (
            <div className="w-full h-full bg-primary flex items-center justify-center text-primary-foreground text-[9px] font-bold">
              WP
            </div>
          )}
          <span className="absolute -bottom-0.5 -right-0.5 w-1.5 h-1.5 rounded-full bg-emerald-500 border border-background" />
        </div>

        <div className="flex flex-col min-w-0 leading-tight">
          <span className="font-semibold text-xs truncate flex items-center gap-1">
            Wilson Pham
            <ExternalLink className="w-2.5 h-2.5 opacity-60 group-hover:opacity-100" />
          </span>
          <span className="text-[10px] font-mono text-muted-foreground group-hover:text-primary flex items-center gap-1">
            <svg className="w-2.5 h-2.5 fill-current" viewBox="0 0 24 24" aria-hidden="true">
              <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
            </svg>
            <span>TheWilsonGlobal</span>
          </span>
        </div>
      </a>
    </div>
  );
};
