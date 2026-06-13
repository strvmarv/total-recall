export interface NavItem {
  path: string;
  label: string;
}

/** Spec nav order: Dashboard · Memory · Knowledge Base · Usage · ✨ Insights · Config. */
export const NAV_ITEMS: NavItem[] = [
  { path: '/', label: 'Dashboard' },
  { path: '/memory', label: 'Memory' },
  { path: '/kb', label: 'Knowledge Base' },
  { path: '/usage', label: 'Usage' },
  { path: '/insights', label: '✨ Insights' },
  { path: '/config', label: 'Config' },
];
