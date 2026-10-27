import { AssistantPanel } from './AssistantPanel';

export function AssistantPage() {
  return (
    <div className="flex h-full flex-col overflow-hidden bg-background">
      <div className="mx-auto flex h-full w-full max-w-6xl flex-col p-3 sm:p-5">
        <div className="bg-card flex-1 min-h-0 overflow-hidden rounded-xl border shadow-xs flex flex-col">
          <AssistantPanel />
        </div>
      </div>
    </div>
  );
}

export default AssistantPage;
