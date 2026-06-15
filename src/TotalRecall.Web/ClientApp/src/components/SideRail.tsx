import { NavLink } from 'react-router-dom';
import { NAV_ITEMS } from './nav';
import { BackendBadge } from './BackendBadge';
import { ThemeToggle } from './ThemeToggle';
import { getBootstrap } from '../lib/bootstrap';

export function SideRail() {
  const { version } = getBootstrap();
  return (
    <aside className="tr-rail" aria-label="Sidebar">
      <div className="tr-brand">total-recall<span className="tr-caret" aria-hidden="true">▌</span></div>
      <nav className="tr-nav" aria-label="Primary">
        {NAV_ITEMS.map((item) => (
          <NavLink
            key={item.path}
            to={item.path}
            end={item.path === '/'}
            className={({ isActive }) => (isActive ? 'tr-nav-link is-active' : 'tr-nav-link')}
          >
            {item.label}
          </NavLink>
        ))}
      </nav>
      <div className="tr-rail-foot">
        <BackendBadge />
        <span className="tr-rail-version">v{version}</span>
        <ThemeToggle />
      </div>
    </aside>
  );
}
