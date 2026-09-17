using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Nemus.Api.Contracts;
using Nemus.Api.Endpoints;
using Nemus.Api.Security;
using Nemus.Infrastructure.Persistence;
using Npgsql;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuracao vinda so do ambiente. Nenhum segredo mora em arquivo do
// repositorio: appsettings.json aqui so tem nivel de log.

string connectionString =
    Environment.GetEnvironmentVariable("NEMUS_DB")
    ?? throw new InvalidOperationException(
        "NEMUS_DB nao esta definida. Em host remoto use SSL Mode=VerifyFull.");

// Falha na subida se o token de instalacao nao existir ou for curto. Ele nao
// abre mais a API - so autoriza criar o primeiro usuario -, mas sem ele a API
// subiria sem nenhum caminho seguro para ganhar dono.
BootstrapGate gate = BootstrapGate.FromEnvironment();

// Kestrel escreve o cabecalho Server depois do middleware, entao remove-lo
// na resposta nao adianta: tem que ser desligado na fonte. Anunciar servidor
// e versao so ajuda quem procura alvo com CVE conhecido.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// O Render publica a porta em PORT. Sem isto o container sobe na 5000 e o
// balanceador nunca encontra o processo.
string? port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// ---------------------------------------------------------------------------
// Servicos.

builder.Services.AddSingleton(_ =>
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    return dataSourceBuilder.Build();
});

builder.Services.AddScoped<AccountRepository>();
builder.Services.AddScoped<CategoryRepository>();
builder.Services.AddScoped<TransactionRepository>();
builder.Services.AddScoped<LedgerQueries>();
builder.Services.AddScoped<ReportQueries>();
builder.Services.AddScoped<RecurringExpenseRepository>();
builder.Services.AddScoped<LedgerIntegrityReader>();
builder.Services.AddScoped<ImportBatchRepository>();
builder.Services.AddScoped<BudgetRepository>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<SessionRepository>();
builder.Services.AddScoped<CreditCardRepository>();
builder.Services.AddScoped<InstallmentRepository>();

// Em memoria e por processo, de proposito: ver LoginThrottle.
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton(gate);

// ITEM 11 - LIMITE DE REQUISICOES. Este e o teto geral por IP, que protege o
// servidor. O freio da SENHA e outro e mais apertado - por usuario e IP, em
// LoginThrottle -, porque 60 palpites por minuto ainda dariam 86 mil por dia.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ITEM 7 - RESTRINGIR ACESSO. Origem cruzada so para o que estiver listado
// em NEMUS_CORS_ORIGINS. Sem a variavel, nenhuma origem e liberada - o
// padrao e negar, nao permitir.
string[] allowedOrigins =
    (Environment.GetEnvironmentVariable("NEMUS_CORS_ORIGINS") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length == 0)
        {
            return;
        }

        policy.WithOrigins(allowedOrigins)
              .WithHeaders("Authorization", "Content-Type")
              .WithMethods("GET", "POST", "PUT", "DELETE");
    }));

// Atras do proxy do Render, sem isto todo IP vira o do balanceador e o
// limitador passa a contar o mundo inteiro como um cliente so.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

WebApplication app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline. A ordem aqui e a seguranca.

app.UseForwardedHeaders();

// ITEM 19 - FORCAR HTTPS. O Render termina TLS na borda e repassa em HTTP,
// entao redirecionar aqui daria laco. O que vale e o HSTS: instrui o
// navegador a nunca mais tentar http neste dominio.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseNemusSecurityHeaders();
app.UseCors();
app.UseRateLimiter();

// ITEM 15 - handler de excecao que nao vaza. Sem isto, uma excecao nao
// tratada devolveria stack trace com nome de tabela e caminho de arquivo.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";

    await context.Response.WriteAsJsonAsync(
        new ErrorResponse("internal_error", "A operacao nao foi concluida.")).ConfigureAwait(false);
}));

// Sonda de saude: sem token de proposito, porque o Render precisa dela para
// saber se o processo esta vivo. Nao revela nada - so responde que esta no ar.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .AllowAnonymous();

// ITEM 6 - AUTENTICACAO NO SERVIDOR. Tudo sob /api exige sessao valida, menos
// as tres rotas que SessionGate lista como abertas. Fica antes do mapeamento
// das rotas, entao nenhum endpoint novo nasce aberto por esquecimento.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api"),
    protectedApp => protectedApp.Use(SessionGate.InvokeAsync));

app.MapAuth();
app.MapAccounts();
app.MapCategories();
app.MapTransactions();
app.MapImports();
app.MapBudget();
app.MapInstallments();
app.MapReports();
app.MapRecurring();

app.MapGet("/api/summary/net-worth", async (
    LedgerQueries queries, CancellationToken cancellationToken) =>
{
    IReadOnlyList<NetWorthRow> rows =
        await queries.ReadNetWorthAsync(cancellationToken).ConfigureAwait(false);

    return Results.Ok(rows
        .Select(r => new NetWorthResponse(r.CurrencyCode, r.Assets, r.Liabilities, r.NetWorth))
        .ToList());
});

// A afirmacao verificavel de que nenhum centavo entrou nem saiu do nada.
app.MapGet("/api/summary/integrity", async (
    LedgerIntegrityReader reader, CancellationToken cancellationToken) =>
{
    LedgerIntegrity integrity = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    return Results.Ok(new IntegrityResponse(
        integrity.IsIntact,
        integrity.SumOfAllBalances,
        integrity.SumInternalBalances,
        integrity.SumExternalBalances,
        integrity.UnbalancedTransactions,
        integrity.UndersizedTransactions,
        integrity.EmptyTransactions,
        integrity.BrokenInstallmentPlans));
});

await app.RunAsync().ConfigureAwait(false);
