import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import './index.css';
import { App } from './App';
import { AppProviders, createQueryClient } from './providers';
import { AssistantPanel } from './features/assistant/AssistantPanel';
import { registerAssistantPanel } from './features/assistant/AssistantSlot';
import { applyTheme, currentTheme } from './lib/theme';

applyTheme(currentTheme());
registerAssistantPanel(() => <AssistantPanel />);
const client = createQueryClient();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <AppProviders client={client}>
        <App />
      </AppProviders>
    </BrowserRouter>
  </StrictMode>,
);
