using Nemus.Domain.Accounts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Nemus.Tests.Database;

/// <summary>
/// Patrimonio liquido contra o banco de verdade.
///
/// Este arquivo existe porque o numero do topo do painel estava errado e
/// nenhum teste percebeu. A 007 somava EQUITY no patrimonio, e como "Saldos
/// iniciais" e EQUITY, o saldo de abertura de toda conta se anulava: quem
/// abria uma conta com R$ 1.000 via patrimonio zero. A 009 corrige, e estes
/// testes seguram a correcao.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NetWorthTests : IAsyncLifetime
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly DateOnly Dia = new(2026, 3, 15);

    private readonly PostgresFixture _fixture;

    public NetWorthTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (PostgresFixture.IsAvailable)
        {
            await _fixture.ResetDataAsync().ConfigureAwait(false);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Account> CriarContaAsync(string nome, AccountType tipo)
    {
        Account conta = Account.Create(nome, tipo, Brl).Value;
        await new AccountRepository(_fixture.DataSource).AddAsync(conta);
        return conta;
    }

    private Task LancarAsync(Transaction transacao) =>
        new TransactionRepository(_fixture.DataSource).AddAsync(transacao);

    private async Task<NetWorthRow> LerAsync()
    {
        IReadOnlyList<NetWorthRow> linhas =
            await new LedgerQueries(_fixture.DataSource).ReadNetWorthAsync();

        return Assert.Single(linhas, linha => linha.CurrencyCode == "BRL");
    }

    /// <summary>O caso que estava errado: saldo de abertura nao pode sumir do patrimonio.</summary>
    [RequiresPostgresFact]
    public async Task Saldo_de_abertura_conta_no_patrimonio()
    {
        Account corrente = await CriarContaAsync("Corrente", AccountType.Asset);

        await LancarAsync(Transaction.OpeningBalance(
            Dia, corrente.Id, SystemAccounts.OpeningBalances, Money.FromUnits(1_000, Brl)).Value);

        NetWorthRow patrimonio = await LerAsync();

        Assert.Equal(100_000, patrimonio.Assets);
        Assert.Equal(100_000, patrimonio.NetWorth);
    }

    [RequiresPostgresFact]
    public async Task Passivo_reduz_o_patrimonio()
    {
        Account corrente = await CriarContaAsync("Corrente", AccountType.Asset);
        Account cartao = await CriarContaAsync("Cartao", AccountType.Liability);

        await LancarAsync(Transaction.OpeningBalance(
            Dia, corrente.Id, SystemAccounts.OpeningBalances, Money.FromUnits(1_000, Brl)).Value);

        // Divida de R$ 300 no cartao: saldo de abertura negativo.
        await LancarAsync(Transaction.OpeningBalance(
            Dia, cartao.Id, SystemAccounts.OpeningBalances, Money.FromUnits(-300, Brl)).Value);

        NetWorthRow patrimonio = await LerAsync();

        Assert.Equal(100_000, patrimonio.Assets);
        Assert.Equal(-30_000, patrimonio.Liabilities);
        Assert.Equal(70_000, patrimonio.NetWorth);
    }

    /// <summary>Despesa reduz o patrimonio; transferencia entre contas proprias nao muda nada.</summary>
    [RequiresPostgresFact]
    public async Task Despesa_reduz_e_transferencia_nao_muda()
    {
        Account corrente = await CriarContaAsync("Corrente", AccountType.Asset);
        Account poupanca = await CriarContaAsync("Poupanca", AccountType.Asset);

        await LancarAsync(Transaction.OpeningBalance(
            Dia, corrente.Id, SystemAccounts.OpeningBalances, Money.FromUnits(1_000, Brl)).Value);
        await LancarAsync(Transaction.Spend(
            Dia, "Mercado", corrente.Id, SystemAccounts.ExternalExpenses, Money.FromUnits(200, Brl)).Value);

        long depoisDaDespesa = (await LerAsync()).NetWorth;

        await LancarAsync(Transaction.Transfer(
            Dia, "Poupar", corrente.Id, poupanca.Id, Money.FromUnits(500, Brl)).Value);

        long depoisDaTransferencia = (await LerAsync()).NetWorth;

        Assert.Equal(80_000, depoisDaDespesa);
        Assert.Equal(depoisDaDespesa, depoisDaTransferencia);
    }

    /// <summary>
    /// Confere a view contra um caminho independente: o saldo conta a conta.
    /// E contra a conta feita a mao, que e o que de fato importa.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Patrimonio_bate_com_a_soma_de_ativo_e_passivo()
    {
        Account corrente = await CriarContaAsync("Corrente", AccountType.Asset);
        Account cartao = await CriarContaAsync("Cartao", AccountType.Liability);

        await LancarAsync(Transaction.OpeningBalance(
            Dia, corrente.Id, SystemAccounts.OpeningBalances, Money.FromMinorUnits(523_417, Brl)).Value);
        await LancarAsync(Transaction.Spend(
            Dia, "Compra no cartao", cartao.Id, SystemAccounts.ExternalExpenses,
            Money.FromMinorUnits(18_990, Brl)).Value);
        await LancarAsync(Transaction.Transfer(
            Dia, "Paga parte da fatura", corrente.Id, cartao.Id, Money.FromMinorUnits(10_000, Brl)).Value);

        IReadOnlyList<AccountBalance> saldos =
            await new AccountRepository(_fixture.DataSource).GetBalancesAsync();

        long somaAtivoPassivo = saldos
            .Where(s => s.Type is AccountType.Asset or AccountType.Liability)
            .Sum(s => s.Balance.MinorUnits);

        Assert.Equal(somaAtivoPassivo, (await LerAsync()).NetWorth);

        // A mao: 5.234,17 de abertura menos 189,90 gastos = 5.044,27. Pagar a
        // fatura so move dinheiro de uma conta propria para outra.
        Assert.Equal(504_427, somaAtivoPassivo);
    }

    /// <summary>
    /// CREATE OR REPLACE VIEW troca as opcoes da view pelas que vierem na
    /// instrucao. Se uma migration recriar uma destas views e esquecer
    /// security_invoker, a view volta a rodar com a permissao de quem a criou
    /// e fura o RLS em silencio. Este teste pega isso.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData("v_account_balances")]
    [InlineData("v_net_worth")]
    [InlineData("v_ledger_integrity")]
    [InlineData("v_budget_entries")]
    [InlineData("v_budget_months")]
    [InlineData("v_budget_ready_to_assign")]
    [InlineData("v_budget_integrity")]
    public async Task Views_continuam_com_security_invoker(string view)
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand("""
            SELECT COALESCE(c.reloptions, ARRAY[]::text[])
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public' AND c.relname = @view AND c.relkind = 'v'
            """);
        command.Parameters.AddWithValue("view", view);

        object? resultado = await command.ExecuteScalarAsync();
        string[] opcoes = Assert.IsType<string[]>(resultado);

        Assert.Contains("security_invoker=true", opcoes);
    }
}
