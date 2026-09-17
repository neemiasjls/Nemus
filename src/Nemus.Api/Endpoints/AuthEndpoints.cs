using System.Globalization;
using Nemus.Api.Contracts;
using Nemus.Api.Security;
using Nemus.Domain.Identity;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Nemus.Infrastructure.Security;
using Npgsql;

namespace Nemus.Api.Endpoints;

internal static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/api/auth").WithTags("Acesso");

        // Aberta: a tela de entrada precisa saber se mostra "entrar" ou
        // "criar o primeiro acesso". O unico dado que sai daqui e esse.
        group.MapGet("/status", async (UserRepository users, CancellationToken cancellationToken) =>
        {
            int count = await users.CountAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new AuthStatusResponse(NeedsFirstAccess: count == 0));
        });

        group.MapPost("/login", async (
            SignInRequest request,
            UserRepository users,
            SessionRepository sessions,
            LoginThrottle throttle,
            HttpContext context,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Auth");

            if (request is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            string username = (request.Username ?? string.Empty).Trim().ToLowerInvariant();
            string key = $"{username}|{context.Connection.RemoteIpAddress}";

            if (throttle.RetryAfter(key) is TimeSpan wait)
            {
                context.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                logger.LogWarning("Login bloqueado por excesso de tentativas.");

                return Results.Json(
                    new ErrorResponse(
                        "auth.too_many_attempts",
                        $"Tentativas demais. Espere {Math.Ceiling(wait.TotalMinutes)} minuto(s) e tente de novo."),
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            User? user = await users.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);

            // Confere a senha mesmo quando o usuario nao existe, contra um
            // hash de mentira: sem isso o tempo de resposta entregaria quais
            // nomes de usuario sao reais.
            PasswordCheck check = PasswordHasher.Verify(
                request.Password, user?.PasswordHash ?? PasswordHasher.DecoyHash);

            if (user is null || !user.IsActive || check == PasswordCheck.Failed)
            {
                throttle.RegisterFailure(key);
                return Results.Json(
                    new ErrorResponse("auth.invalid_credentials", "Usuario ou senha nao conferem."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.Clear(key);

            try
            {
                // O custo do hash sobe com o hardware. Quem entrou com uma
                // senha guardada no custo antigo tem o hash regravado aqui,
                // sem precisar trocar de senha.
                if (check == PasswordCheck.ValidNeedsRehash)
                {
                    user.ChangePassword(PasswordHasher.Hash(request.Password!));
                    await users.SavePasswordAsync(user, cancellationToken).ConfigureAwait(false);
                }

                return await StartSessionAsync(user, sessions, users, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                return Failures.FromPostgres(exception, logger);
            }
        });

        // Criacao do primeiro acesso. So funciona enquanto nao ha usuario
        // nenhum, e ainda assim exige o token do ambiente - senao quem achasse
        // a URL antes de voce viraria o dono do seu razao.
        group.MapPost("/first-access", async (
            SignInRequest request,
            UserRepository users,
            SessionRepository sessions,
            BootstrapGate gate,
            HttpContext context,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Auth");

            if (request is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            if (await users.CountAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                return Results.Conflict(new ErrorResponse(
                    "auth.already_initialized",
                    "Este Nemus ja tem dono. Entre com usuario e senha."));
            }

            if (!gate.Matches(SessionGate.BearerToken(context)))
            {
                logger.LogWarning("Tentativa de primeiro acesso sem o token de instalacao.");

                return Results.Json(
                    new ErrorResponse(
                        "auth.bootstrap_token_invalid",
                        "Token de instalacao ausente ou invalido."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            Result<User> created = BuildUser(request.Username, request.Password);
            if (created.IsFailure)
            {
                return Failures.FromDomain(created.Error);
            }

            try
            {
                await users.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
                return await StartSessionAsync(created.Value, sessions, users, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                return Failures.FromPostgres(exception, logger);
            }
        });

        group.MapGet("/me", (HttpContext context) =>
        {
            User? user = context.CurrentUser();
            Session? session = context.CurrentSession();

            return user is null || session is null
                ? Failures.NotFound("auth.no_session", "Sessao nao encontrada.")
                : Results.Ok(new SessionResponse(user.Username, session.ExpiresAt, user.LastLoginAt));
        });

        group.MapPost("/logout", async (
            HttpContext context,
            SessionRepository sessions,
            CancellationToken cancellationToken) =>
        {
            string? token = SessionGate.BearerToken(context);
            if (!string.IsNullOrEmpty(token))
            {
                await sessions
                    .RevokeAsync(SessionTokens.Fingerprint(token), DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            return Results.NoContent();
        });

        group.MapPost("/password", async (
            ChangePasswordRequest request,
            HttpContext context,
            UserRepository users,
            SessionRepository sessions,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Auth");

            User? user = context.CurrentUser();
            if (request is null || user is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            if (PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash) == PasswordCheck.Failed)
            {
                return Results.Json(
                    new ErrorResponse("auth.invalid_credentials", "A senha atual nao confere."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            Result policy = User.ValidatePassword(request.NewPassword, user.Username);
            if (policy.IsFailure)
            {
                return Failures.FromDomain(policy.Error);
            }

            try
            {
                user.ChangePassword(PasswordHasher.Hash(request.NewPassword!));
                await users.SavePasswordAsync(user, cancellationToken).ConfigureAwait(false);

                // Trocar a senha derruba tudo que estava aberto - inclusive
                // esta aba, que recebe uma sessao nova logo abaixo. Senha nova
                // com sessao velha valendo nao expulsa invasor nenhum.
                await sessions
                    .RevokeAllForUserAsync(user.Id, DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);

                return await StartSessionAsync(user, sessions, users, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                return Failures.FromPostgres(exception, logger);
            }
        });
    }

    private static Result<User> BuildUser(string? username, string? password)
    {
        Result<string> normalized = User.NormalizeUsername(username);
        if (normalized.IsFailure)
        {
            return normalized.Error;
        }

        Result policy = User.ValidatePassword(password, normalized.Value);
        if (policy.IsFailure)
        {
            return policy.Error;
        }

        return User.Create(normalized.Value, PasswordHasher.Hash(password!));
    }

    private static async Task<IResult> StartSessionAsync(
        User user,
        SessionRepository sessions,
        UserRepository users,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string token = SessionTokens.Issue();

        Result<Session> session = Session.Start(user.Id, SessionTokens.Fingerprint(token), now);
        if (session.IsFailure)
        {
            return Failures.FromDomain(session.Error);
        }

        await sessions.AddAsync(session.Value, cancellationToken).ConfigureAwait(false);
        await users.RegisterLoginAsync(user.Id, now, cancellationToken).ConfigureAwait(false);

        // O token so existe nesta resposta. O banco guarda o hash dele.
        return Results.Ok(new SignInResponse(token, user.Username, session.Value.ExpiresAt));
    }
}
