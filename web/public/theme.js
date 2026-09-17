/*
 * Aplica o tema escolhido ANTES da primeira pintura.
 *
 * Por que arquivo separado e nao script inline no index.html: a CSP e
 * script-src 'self', entao script inline seria bloqueado pelo navegador. Um
 * arquivo do proprio dominio, carregado de forma sincrona no head, passa pela
 * CSP e roda antes do body - sem o lampejo de tema errado a cada
 * carregamento para quem escolheu escuro num sistema claro.
 */
(function () {
  try {
    var theme = localStorage.getItem('nemus.theme');
    if (theme === 'light' || theme === 'dark') {
      document.documentElement.setAttribute('data-theme', theme);
    }
  } catch (e) {
    /* armazenamento bloqueado: segue o sistema */
  }
})();
