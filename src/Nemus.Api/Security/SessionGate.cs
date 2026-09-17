using Nemus.Api.Contracts;
using Nemus.Domain.Identity;
using Nemus.Infrastructure.Persistence;
using Nemus.Infrastructure.Security;

namespace Nemus.Api.Security;

/// <summary>
/// Porta de entrada da API: sessao valida no cabecalho Authorization.
///
/// Substitui o token unico do ambiente, que agora so serve para criar o
/// primeiro acesso. A diferenca pratica: token de ambiente nao se revoga sem
/// redeploy, e nao diz quem e quem; sessao expira, se revoga num UPDATE e tem
/// dono.
///
/// Tres rotas ficam abertas, e so tres: descobrir se o app ja tem dono,
/// entrar, e criar o primeiro acesso. Qualquer rota nova sob /api nasce
/// fechada, porque a lista de abertas e explicita.
/// </summary>
internal static class SessionGate
{
    private const string UserItemKey = "nemus.user";
    private const string SessionItemKey = "nemus.session";

    private static readonly string[] PublicPaths =
    [
        "/api/auth/status",
        "/api/auth/login",
        "/api/auth/first-access",
    ];

    public static bool IsPublic(PathString path) =>
        PublicPaths.Any(open => path.Equals(open, StringComparison.OrdinalIgnoreCase));

    /// <summary>O token cru do cabecalho, ou null. Nao e o que fica no banco.</summary>
    public static string? BearerToken(HttpContext context)
    {
        string? header = context.Request.Headers.Authorization;

        return header?.StartsWith("Bearer ", StringComparison.Ordinal) == true
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    public static User? CurrentUser(this HttpContext context) =>
        context.Items.TryGetValue(UserItemKey, out object? user) ? user as User : null;

    public static Session? CurrentSession(this HttpContext context) =>
        context.Items.TryGetValue(SessionItemKey, out object? session) ? session as Session : null;

    public static async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (IsPublic(context.Request.Path))
        {
            await next().ConfigureAwait(false);
            return;
        }

        string? token = BearerToken(context);
        if (string.IsNullOrEmpty(token))
        {
            await RefuseAsync(context).ConfigureAwait(false);
            return;
        }

        var sessions = context.RequestServices.GetRequiredService<SessionRepository>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        AuthenticatedSession? found = await sessions
            .FindActiveAsync(SessionTokens.Fingerprint(token), now, context.RequestAborted)
            .ConfigureAwait(false);

        if (found is null)
        {
            await RefuseAsync(context).ConfigureAwait(false);
            return;
        }

        context.Items[UserItemKey] = found.User;
        context.Items[SessionItemKey] = found.Session;

        // Registrar atividade no maximo de hora em hora: sem essa folga, toda
        // leitura viraria tambem uma escrita.
        if (now - found.Session.LastSeenAt >= SessionRepository.TouchInterval)
        {
            await sessions.TouchAsync(found.Session.Id, now, context.RequestAborted).ConfigureAwait(false);
        }

        await next().ConfigureAwait(false);
    }

    private static async Task RefuseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";

        // Uma mensagem so para ausente, invalida, expirada e revogada: quem
        // esta do lado de fora nao tem direito de saber qual dos quatro e.
        await context.Response.WriteAsJsonAsync(
            new ErrorResponse("unauthorized", "Sessao ausente ou expirada. Entre de novo."))
            .ConfigureAwait(false);
    }
}
