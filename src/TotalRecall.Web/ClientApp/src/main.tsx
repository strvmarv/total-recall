import './styles/fonts';
import './styles/global.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';

const el = document.getElementById('root');
if (!el) throw new Error('Root element #root not found');
createRoot(el).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
