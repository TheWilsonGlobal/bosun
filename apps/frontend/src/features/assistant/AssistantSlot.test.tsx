import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { AssistantSlot, registerAssistantPanel } from './AssistantSlot';

describe('AssistantSlot', () => {
  beforeEach(() => {
    localStorage.clear();
    registerAssistantPanel(() => <div data-testid="assistant-content">Hello Assistant</div>);
  });

  afterEach(() => {
    registerAssistantPanel(null);
  });

  it('renders registered assistant content inside resizable aside', () => {
    render(<AssistantSlot />);
    const aside = screen.getByRole('complementary', { name: 'Assistant' });
    expect(aside).toBeInTheDocument();
    expect(aside).toHaveStyle({ width: '384px' });
    expect(screen.getByTestId('assistant-content')).toBeInTheDocument();
  });

  it('renders nothing when no panel is registered', () => {
    registerAssistantPanel(null);
    const { container } = render(<AssistantSlot />);
    expect(container).toBeEmptyDOMElement();
  });

  it('restores persisted width from localStorage within valid bounds', () => {
    localStorage.setItem('bosun.assistant.width', '450');
    render(<AssistantSlot />);
    const aside = screen.getByRole('complementary', { name: 'Assistant' });
    expect(aside).toHaveStyle({ width: '450px' });
  });

  it('adjusts width using keyboard Arrow keys on the resize handle', () => {
    render(<AssistantSlot />);
    const handle = screen.getByRole('separator', { name: 'Resize Assistant panel' });
    const aside = screen.getByRole('complementary', { name: 'Assistant' });

    // ArrowLeft expands panel width (+20px)
    fireEvent.keyDown(handle, { key: 'ArrowLeft' });
    expect(aside).toHaveStyle({ width: '404px' });
    expect(localStorage.getItem('bosun.assistant.width')).toBe('404');

    // ArrowRight shrinks panel width (-20px)
    fireEvent.keyDown(handle, { key: 'ArrowRight' });
    expect(aside).toHaveStyle({ width: '384px' });
    expect(localStorage.getItem('bosun.assistant.width')).toBe('384');
  });

  it('resets to default width 384px on double-click', () => {
    localStorage.setItem('bosun.assistant.width', '520');
    render(<AssistantSlot />);
    const handle = screen.getByRole('separator', { name: 'Resize Assistant panel' });
    const aside = screen.getByRole('complementary', { name: 'Assistant' });

    expect(aside).toHaveStyle({ width: '520px' });

    fireEvent.doubleClick(handle);
    expect(aside).toHaveStyle({ width: '384px' });
    expect(localStorage.getItem('bosun.assistant.width')).toBe('384');
  });
});
