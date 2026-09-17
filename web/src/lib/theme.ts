/**
 * Tema: claro, escuro ou o do sistema.
 *
 * COMO FUNCIONA. A escolha vira o atributo data-theme no elemento raiz, e o
 * CSS responde a ele. "system" nao escreve atributo nenhum - deixa o
 * prefers-color-scheme decidir, que e o unico jeito de acompanhar o sistema
 * quando ele muda no meio do uso (o Windows trocando para escuro ao
 * anoitecer, por exemplo).
 *
 * A PRIMEIRA PINTURA. public/theme.js aplica o atributo antes do body
 * existir; sem isso, quem escolheu escuro num sistema claro veria um lampejo
 * branco a cada carregamento. Este modulo cuida do resto: trocar em tempo real
 * e manter a barra do navegador no celular com a mesma cor da pagina.
 */

export type Theme = 'light' | 'dark' | 'system';

const STORAGE_KEY = 'nemus.theme';

/** Mesmas cores de --bg em styles.css. */
const BROWSER_BAR_COLOR = { light: '#f3f0e8', dark: '#11110f' } as const;

export function readTheme(): Theme {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === 'light' || stored === 'dark' || stored === 'system') {
      return stored;
    }
  } catch {
    /* navegador com armazenamento bloqueado: cai no padrao */
  }

  return 'system';
}

export function applyTheme(theme: Theme): void {
  const root = document.documentElement;

  if (theme === 'system') {
    root.removeAttribute('data-theme');
  } else {
    root.setAttribute('data-theme', theme);
  }

  syncBrowserBar(theme);

  try {
    localStorage.setItem(STORAGE_KEY, theme);
  } catch {
    /* idem: o tema vale para esta sessao e pronto */
  }
}

/** Reaplica o que estiver guardado. Chamado antes de montar o React. */
export function applyStoredTheme(): void {
  applyTheme(readTheme());
}

function syncBrowserBar(theme: Theme): void {
  document.querySelectorAll<HTMLMetaElement>('meta[name="theme-color"]').forEach((meta) => {
    const isDarkVariant = meta.media.includes('dark');
    meta.content =
      theme === 'system'
        ? isDarkVariant ? BROWSER_BAR_COLOR.dark : BROWSER_BAR_COLOR.light
        : BROWSER_BAR_COLOR[theme];
  });
}
