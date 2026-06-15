import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { TopBar } from './components/TopBar';
import { SectionPlaceholder } from './pages/SectionPlaceholder';
import { Memory } from './pages/Memory';
import { Dashboard } from './pages/Dashboard';
import { KnowledgeBase } from './pages/KnowledgeBase';
import { Usage } from './pages/Usage';
import { Insights } from './pages/Insights';
import { Config } from './pages/Config';

/** Router-agnostic shell (testable with MemoryRouter). */
export function AppShell() {
  return (
    <div className="tr-app">
      <TopBar />
      <main className="tr-main">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/memory" element={<Memory />} />
          <Route path="/kb" element={<KnowledgeBase />} />
          <Route path="/usage" element={<Usage />} />
          <Route path="/insights" element={<Insights />} />
          <Route path="/config" element={<Config />} />
          <Route path="*" element={<SectionPlaceholder title="Not found" note="Unknown route." />} />
        </Routes>
      </main>
    </div>
  );
}

export function App() {
  return (
    <BrowserRouter>
      <AppShell />
    </BrowserRouter>
  );
}
