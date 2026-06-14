import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { TopBar } from './components/TopBar';
import { SectionPlaceholder } from './pages/SectionPlaceholder';
import { MemoryPlaceholder } from './pages/MemoryPlaceholder';
import { Dashboard } from './pages/Dashboard';

/** Router-agnostic shell (testable with MemoryRouter). */
export function AppShell() {
  return (
    <>
      <TopBar />
      <main className="tr-main">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/memory" element={<MemoryPlaceholder />} />
          <Route path="/kb" element={<SectionPlaceholder title="Knowledge Base" />} />
          <Route path="/usage" element={<SectionPlaceholder title="Usage" />} />
          <Route path="/insights" element={<SectionPlaceholder title="Insights" />} />
          <Route path="/config" element={<SectionPlaceholder title="Config" />} />
          <Route path="*" element={<SectionPlaceholder title="Not found" note="Unknown route." />} />
        </Routes>
      </main>
    </>
  );
}

export function App() {
  return (
    <BrowserRouter>
      <AppShell />
    </BrowserRouter>
  );
}
