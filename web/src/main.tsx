import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource-variable/onest';
import './g.css';
import './app.scss';
import './landing.css';
import { App } from './App';
import { initNative } from './nativeInit';

initNative();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
