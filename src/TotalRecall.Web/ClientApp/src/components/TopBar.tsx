import { NavLink } from 'react-router-dom';
import { NAV_ITEMS } from './nav';
import { GlobalSearch } from './GlobalSearch';
import { BackendBadge } from './BackendBadge';

export function TopBar() {
  return (
    <header className="tr-topbar">
      <div className="tr-brand">total-recall</div>
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
      <div className="tr-topbar-right">
        <GlobalSearch />
        <BackendBadge />
      </div>
    </header>
  );
}
