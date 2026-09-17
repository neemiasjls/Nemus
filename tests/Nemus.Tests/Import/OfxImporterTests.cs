using System.Text;
using Nemus.Domain.Accounts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Import;
using Xunit;

namespace Nemus.Tests.Import;

/// <summary>
/// PILAR 3 na camada de traducao. O teste (c) do escopo - reimportar o mesmo
/// arquivo nao duplica - e provado contra o Postgres em IdempotentImportTests;
/// aqui se prova o que vem antes: que a identidade externa gerada a partir do
/// arquivo e estavel e que o razao sai balanceado.
/// </summary>
public sealed class OfxImporterTests
{
    private static readonly Guid ContaCorrente = new("01900000-0000-7000-8000-00000000aaaa");

    private const string Extrato = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102

        <OFX>
        <BANKMSGSRSV1>
        <STMTTRNRS>
        <STMTRS>
        <CURDEF>BRL
        <BANKACCTFROM>
        <BANKID>341
        <ACCTID>12345-6
        <ACCTTYPE>CHECKING
        </BANKACCTFROM>
        <BANKTRANLIST>
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20240115120000[-03:BRT]
        <TRNAMT>-45.90
        <FITID>F-001
        <MEMO>PADARIA
        </STMTTRN>
        <STMTTRN>
        <TRNTYPE>CREDIT
        <DTPOSTED>20240105080000[-03:BRT]
        <TRNAMT>3500.00
        <FITID>F-002
        <MEMO>SALARIO
        </STMTTRN>
        </BANKTRANLIST>
        </STMTRS>
        </STMTTRNRS>
        </BANKMSGSRSV1>
        </OFX>
        """;

    private static OfxStatement Ler(string texto)
    {
        Result<OfxNode> arvore = OfxParser.Parse(Encoding.ASCII.GetBytes(texto));
        Assert.True(arvore.IsSuccess, $"{arvore.Error}");

        Result<IReadOnlyList<OfxStatement>> extratos = OfxStatementReader.Read(arvore.Value);
        Assert.True(extratos.IsSuccess, $"{extratos.Error}");

        return extratos.Value[0];
    }

    private static OfxMapping Mapear(string texto = Extrato)
    {
        Result<OfxMapping> resultado = OfxImporter.Map(Ler(texto), ContaCorrente);
        Assert.True(resultado.IsSuccess, $"{resultado.Error}");
        return resultado.Value;
    }

    [Fact]
    public void Gera_uma_transacao_por_linha()
    {
        OfxMapping mapa = Mapear();

        Assert.Equal(2, mapa.Transactions.Count);
        Assert.Equal(0, mapa.DuplicatesWithinFile);
    }

    /// <summary>
    /// A invariante central. Se alguma linha do extrato gerasse transacao
    /// desbalanceada, o razao inteiro deixaria de fechar em zero.
    /// </summary>
    [Fact]
    public void Toda_transacao_sai_balanceada()
    {
        OfxMapping mapa = Mapear();

        foreach (Transaction transacao in mapa.Transactions)
        {
            Assert.Equal(0, transacao.Balance.MinorUnits);
            Assert.Equal(2, transacao.Entries.Count);
        }
    }

    [Fact]
    public void Saida_debita_a_conta_e_credita_despesa_externa()
    {
        Transaction despesa = Mapear().Transactions.Single(t => t.Description == "PADARIA");

        Entry naConta = despesa.Entries.Single(e => e.AccountId == ContaCorrente);
        Entry contraparte = despesa.Entries.Single(e => e.AccountId != ContaCorrente);

        Assert.Equal(-4590, naConta.Amount.MinorUnits);
        Assert.Equal(4590, contraparte.Amount.MinorUnits);
        Assert.Equal(SystemAccounts.ExternalExpenses, contraparte.AccountId);
    }

    [Fact]
    public void Entrada_credita_a_conta_e_debita_receita_externa()
    {
        Transaction receita = Mapear().Transactions.Single(t => t.Description == "SALARIO");

        Entry naConta = receita.Entries.Single(e => e.AccountId == ContaCorrente);
        Entry contraparte = receita.Entries.Single(e => e.AccountId != ContaCorrente);

        Assert.Equal(350000, naConta.Amount.MinorUnits);
        Assert.Equal(-350000, contraparte.Amount.MinorUnits);
        Assert.Equal(SystemAccounts.ExternalRevenue, contraparte.AccountId);
    }

    /// <summary>
    /// A soma de TODAS as pernas de TODAS as transacoes de um lote precisa
    /// ser zero. E o mesmo invariante do razao, verificado no lote inteiro.
    /// </summary>
    [Fact]
    public void Soma_de_todas_as_pernas_do_lote_e_zero()
    {
        OfxMapping mapa = Mapear();

        long soma = mapa.Transactions
            .SelectMany(t => t.Entries)
            .Sum(e => e.Amount.MinorUnits);

        Assert.Equal(0, soma);
    }

    [Fact]
    public void Identidade_externa_carrega_fitid_e_conta_de_origem()
    {
        Transaction primeira = Mapear().Transactions.Single(t => t.Description == "PADARIA");

        Assert.NotNull(primeira.External);
        Assert.Equal(ImportSource.Ofx, primeira.External!.Value.Source);
        Assert.Equal("F-001", primeira.External.Value.ExternalId);
        Assert.Equal("12345-6", primeira.External.Value.AccountRef);
        Assert.Equal(ImportSource.Ofx, primeira.Source);
    }

    /// <summary>
    /// O FITID so e unico dentro de uma conta de uma instituicao: dois bancos
    /// podem emitir o mesmo. Por isso a chave inclui a conta de origem - sem
    /// ela, uma linha de um banco esconderia a linha de outro.
    /// </summary>
    [Fact]
    public void Mesmo_fitid_em_contas_diferentes_gera_chaves_diferentes()
    {
        OfxMapping doPrimeiro = Mapear();

        string outraConta = Extrato.Replace(
            "<ACCTID>12345-6", "<ACCTID>99999-9", StringComparison.Ordinal);
        OfxMapping doSegundo = Mapear(outraConta);

        string chaveA = doPrimeiro.Transactions[0].External!.Value.IdempotencyKey;
        string chaveB = doSegundo.Transactions[0].External!.Value.IdempotencyKey;

        Assert.NotEqual(chaveA, chaveB);
    }

    /// <summary>
    /// Traduzir o mesmo arquivo duas vezes tem que produzir exatamente as
    /// mesmas chaves. Se a identidade dependesse de algo variavel - relogio,
    /// UUID novo, ordem - a reimportacao duplicaria tudo.
    /// </summary>
    [Fact]
    public void Traduzir_o_mesmo_arquivo_duas_vezes_produz_as_mesmas_chaves()
    {
        string[] primeira = Mapear().Transactions
            .Select(t => t.External!.Value.IdempotencyKey).Order().ToArray();

        string[] segunda = Mapear().Transactions
            .Select(t => t.External!.Value.IdempotencyKey).Order().ToArray();

        Assert.Equal(primeira, segunda);
    }

    /// <summary>
    /// Cada traducao gera ids proprios. Nao e defeito: a chave primaria e
    /// interna e quem impede duplicata e o indice sobre a identidade externa,
    /// nao o id. Este teste existe para deixar isso explicito - alguem podia
    /// se sentir tentado a derivar o id do FITID e criar acoplamento entre a
    /// chave primaria e um dado de terceiro.
    /// </summary>
    [Fact]
    public void Ids_internos_sao_novos_a_cada_traducao()
    {
        Guid primeiro = Mapear().Transactions[0].Id;
        Guid segundo = Mapear().Transactions[0].Id;

        Assert.NotEqual(primeiro, segundo);
    }

    [Fact]
    public void Deduplica_linhas_repetidas_dentro_do_proprio_arquivo()
    {
        // Acontece quando a pessoa baixa dois periodos que se sobrepoem e
        // junta os arquivos.
        string repetido = Extrato.Replace(
            "</BANKTRANLIST>",
            """
            <STMTTRN>
            <TRNTYPE>DEBIT
            <DTPOSTED>20240115120000[-03:BRT]
            <TRNAMT>-45.90
            <FITID>F-001
            <MEMO>PADARIA
            </STMTTRN>
            </BANKTRANLIST>
            """,
            StringComparison.Ordinal);

        OfxMapping mapa = Mapear(repetido);

        Assert.Equal(2, mapa.Transactions.Count);
        Assert.Equal(1, mapa.DuplicatesWithinFile);
    }

    [Fact]
    public void Data_do_lancamento_e_a_do_extrato()
    {
        Transaction despesa = Mapear().Transactions.Single(t => t.Description == "PADARIA");

        Assert.Equal(new DateOnly(2024, 1, 15), despesa.OccurredOn);
    }

    [Fact]
    public void Recusa_mapear_sem_conta_de_destino()
    {
        Result<OfxMapping> resultado = OfxImporter.Map(Ler(Extrato), Guid.Empty);

        Assert.True(resultado.IsFailure);
        Assert.Equal("ofx.account_required", resultado.Error.Code);
    }

    [Fact]
    public void Vincula_o_lote_de_importacao_quando_informado()
    {
        var lote = new Guid("01900000-0000-7000-8000-00000000bbbb");

        Result<OfxMapping> resultado = OfxImporter.Map(Ler(Extrato), ContaCorrente, lote);

        Assert.True(resultado.IsSuccess);
        Assert.All(resultado.Value.Transactions, t => Assert.Equal(lote, t.ImportBatchId));
    }

    /// <summary>
    /// O hash do arquivo e atalho barato para nao reprocessar o mesmo
    /// download; nao e o que garante correcao. Um byte diferente no cabecalho
    /// muda o hash, e ai o FITID de cada linha continua segurando.
    /// </summary>
    [Fact]
    public void Hash_do_arquivo_e_estavel_e_sensivel_a_conteudo()
    {
        byte[] original = Encoding.ASCII.GetBytes(Extrato);
        byte[] igual = Encoding.ASCII.GetBytes(Extrato);
        byte[] diferente = Encoding.ASCII.GetBytes(Extrato.Replace("PADARIA", "MERCADO", StringComparison.Ordinal));

        Assert.Equal(OfxImporter.ComputeFileHash(original), OfxImporter.ComputeFileHash(igual));
        Assert.NotEqual(OfxImporter.ComputeFileHash(original), OfxImporter.ComputeFileHash(diferente));
        Assert.Matches("^[0-9a-f]{64}$", OfxImporter.ComputeFileHash(original));
    }

    /// <summary>
    /// O formato do hash precisa passar no CHECK de import_batches, senao a
    /// gravacao falharia so em producao.
    /// </summary>
    [Fact]
    public void Hash_respeita_o_formato_exigido_pelo_banco()
    {
        string hash = OfxImporter.ComputeFileHash(Encoding.ASCII.GetBytes("qualquer coisa"));

        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(hash, char.IsUpper);
    }

    [Fact]
    public void Lancamento_de_valor_zero_e_recusado_pelo_dominio()
    {
        // Uma perna de valor zero nao pode existir: o CHECK do banco proibe,
        // e o dominio precisa recusar antes.
        string comZero = Extrato.Replace("<TRNAMT>-45.90", "<TRNAMT>0.00", StringComparison.Ordinal);

        Result<OfxMapping> resultado = OfxImporter.Map(Ler(comZero), ContaCorrente);

        Assert.True(resultado.IsFailure);
    }
}
