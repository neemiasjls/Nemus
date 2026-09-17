import React from 'react';
import ReactDOM from 'react-dom/client';
import { App } from './App';
import { applyStoredTheme } from './lib/theme';
import './styles.css';

// public/theme.js ja aplicou o tema antes da primeira pintura. Aqui ele e
// reaplicado para sincronizar tambem a cor da barra do navegador.
applyStoredTheme();

const root = document.getElementById('root');

if (!root) {
  throw new Error('Elemento #root nao encontrado.');
}

ReactDOM.createRoot(root).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
