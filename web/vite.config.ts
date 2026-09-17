import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import { cloudflare } from '@cloudflare/vite-plugin';

export default defineConfig(({ command, mode }) => {
  // O Vite INCORPORA VITE_* no bundle na hora de compilar. Build de producao
  // sem a URL da API gera um site que chama a si mesmo em vez da API - foi
  // assim que o primeiro deploy saiu quebrado, e o script "deploy" que o
  // wrangler criou roda exatamente esse build sem a variavel. Melhor falhar
  // aqui, dizendo o que fazer, do que publicar quebrado em silencio.
  //
  // So vale para build. O servidor de desenvolvimento (e o npm run demo)
  // continuam subindo sem ela.
  if (command === 'build') {
    const api = process.env.VITE_NEMUS_API ?? loadEnv(mode, process.cwd(), 'VITE_').VITE_NEMUS_API;

    if (!api) {
      throw new Error(
        'VITE_NEMUS_API nao definida. O build de producao precisa da URL da API, por exemplo: ' +
          'VITE_NEMUS_API="https://nemus-api.onrender.com" npm run build',
      );
    }
  }

  return {
    // cloudflare(): adicionado pelo wrangler. E o que empacota o site para a
    // Cloudflare e gera dist/wrangler.json.
    plugins: [react(), cloudflare()],
    build: { outDir: 'dist', sourcemap: false },
  };
});
