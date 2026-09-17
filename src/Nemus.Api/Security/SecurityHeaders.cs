namespace Nemus.Api.Security;

/// <summary>
/// Cabecalhos de seguranca da resposta.
///
/// Esta API so devolve JSON, entao o conjunto util aqui e menor do que o de
/// um site: nao ha HTML para sofrer XSS nem formulario para ser enquadrado.
/// O que resta ainda vale.
/// </summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseNemusSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            IHeaderDictionary headers = context.Response.Headers;

            // Impede o navegador de adivinhar tipo de conteudo. Sem isto,
            // uma resposta JSON com texto controlado pelo usuario pode ser
            // reinterpretada como HTML e executar script.
            headers["X-Content-Type-Options"] = "nosniff";

            // Nao ha pagina para embutir, entao negar enquadramento e de graca.
            headers["X-Frame-Options"] = "DENY";

            // CSP restritiva: a API nao serve documento nenhum.
            headers["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";

            // Nao vazar a URL da API para terceiros via Referer.
            headers["Referrer-Policy"] = "no-referrer";

            // Extrato bancario nao entra em cache compartilhado.
            headers["Cache-Control"] = "no-store";

            await next().ConfigureAwait(false);
        });
    }
}
