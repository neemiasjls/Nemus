using Nemus.Domain.Accounts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Nemus.Tests.Database;

/// <summary>
/// TESTE (c) - reimportar o mesmo ID externo nao duplica.
///
/// O caso que de fato quebra sistemas nao e a transacao duplicada: e a
/// PERNA duplicada. Quem faz "ON CONFLICT DO NOTHING" no cabecalho e depois
/// insere as pernas mesmo assim acaba com uma transacao de quatro pernas
/// somando o dobro - e ai o razao esta desbalanceado sem ninguem ver.
/// Por isso aqui se verifica contagem de transacoes, contagem de pernas,
/// saldos e integridade global.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IdempotentImportTests : IAsyncLifetime
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly DateOnly Hoje = new(2026, 3, 15);

    private readonly PostgresFixture _fixture;

    public IdempotentImportTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (PostgresFixture.IsAvailable)
        {
            await _fixture.ResetDataAsync().ConfigureAwait(false);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // -----------------------------------------------------------------------

    [RequiresPostgresFact]
    public async Task Reimportar_o_mesmo_arquivo_nao_muda_nada()
    {
        Guid conta = await CriarContaAsync("Corrente Itau", "0001-12345");
        var repository = new TransactionRepository(_fixture.DataSource);
        var integrity = new LedgerIntegrityReader(_fixture.DataSource);
        var accounts = new AccountRepository(_fixture.DataSource);

        List<Transaction> arquivo = SimularExtrato(conta, "0001-12345", 25);

        ImportSummary primeira = await repository.ImportAsync(arquivo);

        Assert.Equal(25, primeira.Inserted);
        Assert.Equal(0, primeira.SkippedAsDuplicate);

        int transacoesDepoisDaPrimeira = await repository.CountAsync();
        int pernasDepoisDaPrimeira = await repository.CountEntriesAsync();
        long saldoDepoisDaPrimeira =
            (await accounts.GetBalanceAsync(conta, Brl)).MinorUnits;

        // Mesmo arquivo, objetos novos com os MESMOS FITIDs - e o que
        // acontece quando o usuario reimporta o extrato do mes.
        List<Transaction> mesmoArquivoDeNovo = SimularExtrato(conta, "0001-12345", 25);
        ImportSummary segunda = await repository.ImportAsync(mesmoArquivoDeNovo);

        Assert.Equal(0, segunda.Inserted);
        Assert.Equal(25, segunda.SkippedAsDuplicate);

        Assert.Equal(transacoesDepoisDaPrimeira, await repository.CountAsync());

        // O que realmente importa: as pernas nao dobraram.
        Assert.Equal(pernasDepoisDaPrimeira, await repository.CountEntriesAsync());

        Assert.Equal(
            saldoDepoisDaPrimeira,
            (await accounts.GetBalanceAsync(conta, Brl)).MinorUnits);

        LedgerIntegrity estado = await integrity.ReadAsync();
        Assert.True(estado.IsIntact, estado.Describe());
    }

    [RequiresPostgresFact]
    public async Task Dez_reimportacoes_seguidas_deixam_o_banco_identico()
    {
        Guid conta = await CriarContaAsync("Corrente", "0001-99999");
        var repository = new TransactionRepository(_fixture.DataSource);

        await repository.ImportAsync(SimularExtrato(conta, "0001-99999", 10));

        int transacoes = await repository.CountAsync();
        int pernas = await repository.CountEntriesAsync();

        for (int i = 0; i < 10; i++)
        {
            ImportSummary resumo = await repository.ImportAsync(
                SimularExtrato(conta, "0001-99999", 10));

            Assert.Equal(0, resumo.Inserted);
            Assert.Equal(10, resumo.SkippedAsDuplicate);
        }

        Assert.Equal(transacoes, await repository.CountAsync());
        Assert.Equal(pernas, await repository.CountEntriesAsync());
        Assert.True((await new LedgerIntegrityReader(_fixture.DataSource).ReadAsync()).IsIntact);
    }

    [RequiresPostgresFact]
    public async Task Mesmo_fitid_em_contas_diferentes_sao_transacoes_diferentes()
    {
        // O FITID do OFX so e unico dentro de uma conta de uma instituicao.
        // Se a chave de idempotencia fosse so o FITID, a segunda conta
        // perderia lancamentos em silencio.
        Guid contaItau = await CriarContaAsync("Corrente Itau", "0001-11111");
        Guid contaNubank = await CriarContaAsync("Corrente Nubank", "0002-22222");

        var repository = new TransactionRepository(_fixture.DataSource);

        Transaction noItau = ComFitid(contaItau, "0001-11111", "FITID-COLIDE", 10000);
        Transaction noNubank = ComFitid(contaNubank, "0002-22222", "FITID-COLIDE", 20000);

        ImportSummary resumo = await repository.ImportAsync([noItau, noNubank]);

        Assert.Equal(2, resumo.Inserted);
        Assert.Equal(0, resumo.SkippedAsDuplicate);
        Assert.Equal(2, await repository.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task Transacao_importada_e_apagada_nao_ressuscita_na_reimportacao()
    {
        Guid conta = await CriarContaAsync("Corrente", "0001-33333");
        var repository = new TransactionRepository(_fixture.DataSource);

        Transaction original = ComFitid(conta, "0001-33333", "FITID-APAGADO", 5000);
        await repository.ImportAsync([original]);

        await using (NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            "UPDATE transactions SET deleted_at = now() WHERE id = @id"))
        {
            command.Parameters.AddWithValue("id", original.Id);
            await command.ExecuteNonQueryAsync();
        }

        // Voce apagou de proposito. Reimportar o arquivo nao pode desfazer
        // isso - por isso o indice unico NAO filtra por deleted_at.
        ImportSummary resumo = await repository.ImportAsync(
            [ComFitid(conta, "0001-33333", "FITID-APAGADO", 5000)]);

        Assert.Equal(0, resumo.Inserted);
        Assert.Equal(1, resumo.SkippedAsDuplicate);
        Assert.Equal(0, await repository.CountAsync());
        Assert.Equal(1, await repository.CountAsync(includeDeleted: true));
    }

    [RequiresPostgresFact]
    public async Task Extrato_do_mes_seguinte_repetindo_lancamentos_nao_duplica()
    {
        // Caso real: o extrato de abril vem com os ultimos dias de marco.
        Guid conta = await CriarContaAsync("Corrente", "0001-44444");
        var repository = new TransactionRepository(_fixture.DataSource);

        List<Transaction> marco = SimularExtrato(conta, "0001-44444", 10);
        await repository.ImportAsync(marco);

        List<Transaction> abril = SimularExtrato(conta, "0001-44444", 10)
            .Concat(SimularExtrato(conta, "0001-44444", 15).Skip(10))
            .ToList();

        ImportSummary resumo = await repository.ImportAsync(abril);

        Assert.Equal(5, resumo.Inserted);
        Assert.Equal(10, resumo.SkippedAsDuplicate);
        Assert.Equal(15, await repository.CountAsync());
        Assert.True((await new LedgerIntegrityReader(_fixture.DataSource).ReadAsync()).IsIntact);
    }

    [RequiresPostgresFact]
    public async Task Lancamentos_manuais_nao_colidem_entre_si()
    {
        // Sem external_id o indice unico parcial nao se aplica: dois almocos
        // de R$ 30 no mesmo dia sao duas despesas, nao uma duplicata.
        Guid conta = await CriarContaAsync("Corrente", "0001-55555");
        var repository = new TransactionRepository(_fixture.DataSource);

        Transaction primeiro = Transaction.Spend(
            Hoje, "Almoco", conta, SystemAccounts.ExternalExpenses, Money.FromUnits(30, Brl)).Value;
        Transaction segundo = Transaction.Spend(
            Hoje, "Almoco", conta, SystemAccounts.ExternalExpenses, Money.FromUnits(30, Brl)).Value;

        await repository.AddAsync(primeiro);
        await repository.AddAsync(segundo);

        Assert.Equal(2, await repository.CountAsync());
        Assert.True((await new LedgerIntegrityReader(_fixture.DataSource).ReadAsync()).IsIntact);
    }

    [RequiresPostgresFact]
    public async Task ExistsAsync_reconhece_o_que_ja_entrou()
    {
        Guid conta = await CriarContaAsync("Corrente", "0001-66666");
        var repository = new TransactionRepository(_fixture.DataSource);

        ExternalReference referencia =
            ExternalReference.Create(ImportSource.Ofx, "FITID-1", "0001-66666").Value;

        Assert.False(await repository.ExistsAsync(referencia));

        await repository.ImportAsync([ComFitid(conta, "0001-66666", "FITID-1", 1000)]);

        Assert.True(await repository.ExistsAsync(referencia));
    }

    // -----------------------------------------------------------------------

    private static List<Transaction> SimularExtrato(Guid conta, string accountRef, int linhas)
    {
        var transacoes = new List<Transaction>(linhas);

        for (int i = 0; i < linhas; i++)
        {
            // FITID deterministico: reimportar produz exatamente os mesmos.
            transacoes.Add(ComFitid(conta, accountRef, $"FITID-{i:D4}", 1000 + (i * 137)));
        }

        return transacoes;
    }

    private static Transaction ComFitid(
        Guid conta, string accountRef, string fitid, long minorUnits)
    {
        ExternalReference referencia =
            ExternalReference.Create(ImportSource.Ofx, fitid, accountRef).Value;

        return Transaction.Spend(
            Hoje,
            $"Lancamento {fitid}",
            conta,
            SystemAccounts.ExternalExpenses,
            Money.FromMinorUnits(minorUnits, Brl),
            external: referencia).Value;
    }

    private async Task<Guid> CriarContaAsync(string nome, string externalRef)
    {
        Account conta = Account.Create(
            nome, AccountType.Asset, Brl, externalRef: externalRef).Value;

        await new AccountRepository(_fixture.DataSource).AddAsync(conta);
        return conta.Id;
    }
}
