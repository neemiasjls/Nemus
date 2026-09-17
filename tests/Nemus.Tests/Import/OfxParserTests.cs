using System.Text;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Import;
using Xunit;

namespace Nemus.Tests.Import;

/// <summary>
/// O leitor de OFX e a fronteira com o mundo real, e o mundo real aqui e
/// banco brasileiro: SGML com tag aberta, Windows-1252 declarado como ASCII,
/// virgula onde a especificacao pede ponto e fuso no meio da data.
/// </summary>
public sealed class OfxParserTests
{
    // Formato que Itau, Bradesco e Santander emitem: OFX 1.0.2 SGML,
    // nenhuma tag de valor fechada.
    private const string ExtratoSgml = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE

        <OFX>
        <SIGNONMSGSRSV1>
        <SONRS>
        <STATUS>
        <CODE>0
        <SEVERITY>INFO
        </STATUS>
        <DTSERVER>20240201120000[-03:BRT]
        <LANGUAGE>POR
        </SONRS>
        </SIGNONMSGSRSV1>
        <BANKMSGSRSV1>
        <STMTTRNRS>
        <TRNUID>1
        <STMTRS>
        <CURDEF>BRL
        <BANKACCTFROM>
        <BANKID>341
        <ACCTID>12345-6
        <ACCTTYPE>CHECKING
        </BANKACCTFROM>
        <BANKTRANLIST>
        <DTSTART>20240101000000[-03:BRT]
        <DTEND>20240131235959[-03:BRT]
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20240115120000[-03:BRT]
        <TRNAMT>-45.90
        <FITID>202401150001
        <MEMO>PAG*IFOOD SAO PAULO BR
        </STMTTRN>
        <STMTTRN>
        <TRNTYPE>CREDIT
        <DTPOSTED>20240105080000[-03:BRT]
        <TRNAMT>3500.00
        <FITID>202401050002
        <MEMO>SALARIO
        </STMTTRN>
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20240131210000[-03:BRT]
        <TRNAMT>-120.00
        <FITID>202401310003
        <MEMO>SUPERMERCADO
        </STMTTRN>
        </BANKTRANLIST>
        <LEDGERBAL>
        <BALAMT>3334.10
        <DTASOF>20240131235959[-03:BRT]
        </LEDGERBAL>
        </STMTRS>
        </STMTTRNRS>
        </BANKMSGSRSV1>
        </OFX>
        """;

    private static OfxStatement LerUnico(string texto, Encoding? codificacao = null)
    {
        byte[] bytes = (codificacao ?? Encoding.ASCII).GetBytes(texto);

        Result<OfxNode> arvore = OfxParser.Parse(bytes);
        Assert.True(arvore.IsSuccess, $"parse falhou: {arvore.Error}");

        Result<IReadOnlyList<OfxStatement>> extratos = OfxStatementReader.Read(arvore.Value);
        Assert.True(extratos.IsSuccess, $"leitura falhou: {extratos.Error}");

        return Assert.Single(extratos.Value);
    }

    [Fact]
    public void Le_sgml_com_tags_nao_fechadas()
    {
        OfxStatement extrato = LerUnico(ExtratoSgml);

        Assert.Equal("341", extrato.BankId);
        Assert.Equal("12345-6", extrato.AccountId);
        Assert.Equal("CHECKING", extrato.AccountType);
        Assert.Equal(Currency.Brl, extrato.Currency);
        Assert.Equal(3, extrato.Entries.Count);
    }

    [Fact]
    public void Le_valores_como_inteiro_com_sinal()
    {
        OfxStatement extrato = LerUnico(ExtratoSgml);

        Assert.Equal(-4590, extrato.Entries[0].Amount.MinorUnits);
        Assert.Equal(350000, extrato.Entries[1].Amount.MinorUnits);
        Assert.Equal(-12000, extrato.Entries[2].Amount.MinorUnits);
    }

    [Fact]
    public void Le_saldo_de_fechamento()
    {
        OfxStatement extrato = LerUnico(ExtratoSgml);

        Assert.NotNull(extrato.ClosingBalance);
        Assert.Equal(333410, extrato.ClosingBalance!.Value.MinorUnits);
    }

    [Fact]
    public void Le_periodo_do_extrato()
    {
        OfxStatement extrato = LerUnico(ExtratoSgml);

        Assert.Equal(new DateOnly(2024, 1, 1), extrato.StartsOn);
        Assert.Equal(new DateOnly(2024, 1, 31), extrato.EndsOn);
    }

    /// <summary>
    /// O caso que motiva a decisao de nao converter para UTC. Uma compra as
    /// 21h do dia 31 em BRT seria dia 1o do mes seguinte em UTC, e
    /// occurred_on e a competencia que o orcamento usa - a despesa cairia no
    /// mes errado bem na virada.
    /// </summary>
    [Fact]
    public void Data_permanece_a_do_extrato_mesmo_perto_da_virada_do_mes()
    {
        OfxStatement extrato = LerUnico(ExtratoSgml);

        OfxEntry ultima = extrato.Entries[2];
        Assert.Equal(new DateOnly(2024, 1, 31), ultima.PostedOn);
        Assert.NotEqual(new DateOnly(2024, 2, 1), ultima.PostedOn);
    }

    [Fact]
    public void Le_ofx_2_que_e_xml_de_verdade()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <?OFX OFXHEADER="200" VERSION="220" SECURITY="NONE" OLDFILEUID="NONE" NEWFILEUID="NONE"?>
            <OFX>
              <BANKMSGSRSV1>
                <STMTTRNRS>
                  <STMTRS>
                    <CURDEF>BRL</CURDEF>
                    <BANKACCTFROM>
                      <BANKID>260</BANKID>
                      <ACCTID>98765</ACCTID>
                      <ACCTTYPE>CHECKING</ACCTTYPE>
                    </BANKACCTFROM>
                    <BANKTRANLIST>
                      <STMTTRN>
                        <TRNTYPE>DEBIT</TRNTYPE>
                        <DTPOSTED>20240210103000</DTPOSTED>
                        <TRNAMT>-19.90</TRNAMT>
                        <FITID>XPTO-1</FITID>
                        <MEMO>ASSINATURA</MEMO>
                      </STMTTRN>
                    </BANKTRANLIST>
                  </STMTRS>
                </STMTTRNRS>
              </BANKMSGSRSV1>
            </OFX>
            """;

        OfxStatement extrato = LerUnico(xml, Encoding.UTF8);

        Assert.Equal("98765", extrato.AccountId);
        OfxEntry linha = Assert.Single(extrato.Entries);
        Assert.Equal(-1990, linha.Amount.MinorUnits);
        Assert.Equal("ASSINATURA", linha.Description);
        Assert.Equal(new DateOnly(2024, 2, 10), linha.PostedOn);
    }

    /// <summary>
    /// CHARSET:1252 declarado junto de ENCODING:USASCII e o padrao de banco
    /// brasileiro. Sem decodificar direito, "PADARIA SAO JOAO" com til vira
    /// caractere corrompido e fica assim no banco de dados para sempre.
    /// </summary>
    [Fact]
    public void Decodifica_windows_1252_com_acento()
    {
        string comAcento = ExtratoSgml.Replace(
            "PAG*IFOOD SAO PAULO BR", "PADARIA SÃO JOÃO - AÇÚCAR", StringComparison.Ordinal);

        Encoding cp1252 = CodificacaoLatina();
        OfxStatement extrato = LerUnico(comAcento, cp1252);

        Assert.Equal("PADARIA SÃO JOÃO - AÇÚCAR", extrato.Entries[0].Description);
    }

    [Fact]
    public void Aceita_virgula_como_separador_decimal()
    {
        // Parte dos bancos emite virgula, contrariando a especificacao.
        string comVirgula = ExtratoSgml.Replace("<TRNAMT>-45.90", "<TRNAMT>-45,90", StringComparison.Ordinal);

        OfxStatement extrato = LerUnico(comVirgula);

        Assert.Equal(-4590, extrato.Entries[0].Amount.MinorUnits);
    }

    [Fact]
    public void Aceita_uma_casa_decimal()
    {
        string umaCasa = ExtratoSgml.Replace("<TRNAMT>-45.90", "<TRNAMT>-45.9", StringComparison.Ordinal);

        OfxStatement extrato = LerUnico(umaCasa);

        Assert.Equal(-4590, extrato.Entries[0].Amount.MinorUnits);
    }

    [Fact]
    public void Aceita_valor_sem_casa_decimal()
    {
        string inteiro = ExtratoSgml.Replace("<TRNAMT>-45.90", "<TRNAMT>-45", StringComparison.Ordinal);

        OfxStatement extrato = LerUnico(inteiro);

        Assert.Equal(-4500, extrato.Entries[0].Amount.MinorUnits);
    }

    /// <summary>
    /// O caso que quebra quem converte SGML para XML com expressao regular:
    /// "&amp;" e "&lt;" crus dentro da descricao. Aparece em nome de
    /// estabelecimento com mais frequencia do que se espera.
    /// </summary>
    [Fact]
    public void Aguenta_e_comercial_cru_na_descricao()
    {
        string comEComercial = ExtratoSgml.Replace(
            "<MEMO>SALARIO", "<MEMO>LOJA A & B COMERCIO", StringComparison.Ordinal);

        OfxStatement extrato = LerUnico(comEComercial);

        Assert.Equal("LOJA A & B COMERCIO", extrato.Entries[1].Description);
    }

    [Fact]
    public void Prefere_memo_a_name_na_descricao()
    {
        string comAmbos = ExtratoSgml.Replace(
            "<MEMO>PAG*IFOOD SAO PAULO BR",
            "<NAME>COMPRA CARTAO\n<MEMO>PAG*IFOOD SAO PAULO BR",
            StringComparison.Ordinal);

        OfxStatement extrato = LerUnico(comAmbos);

        Assert.Equal("PAG*IFOOD SAO PAULO BR", extrato.Entries[0].Description);
        Assert.Equal("COMPRA CARTAO", extrato.Entries[0].Name);
    }

    [Fact]
    public void Usa_name_quando_nao_ha_memo()
    {
        string semMemo = ExtratoSgml.Replace(
            "<MEMO>PAG*IFOOD SAO PAULO BR", "<NAME>TRANSFERENCIA PIX", StringComparison.Ordinal);

        OfxStatement extrato = LerUnico(semMemo);

        Assert.Equal("TRANSFERENCIA PIX", extrato.Entries[0].Description);
    }

    [Fact]
    public void Recusa_lancamento_sem_fitid()
    {
        string semFitId = ExtratoSgml.Replace(
            "<FITID>202401150001\n", string.Empty, StringComparison.Ordinal);

        Result<OfxNode> arvore = OfxParser.Parse(Encoding.ASCII.GetBytes(semFitId));
        Assert.True(arvore.IsSuccess);

        Result<IReadOnlyList<OfxStatement>> extratos = OfxStatementReader.Read(arvore.Value);

        Assert.True(extratos.IsFailure);
        Assert.Equal("ofx.missing_fitid", extratos.Error.Code);
    }

    [Fact]
    public void Recusa_arquivo_que_nao_e_ofx()
    {
        Result<OfxNode> resultado = OfxParser.Parse(Encoding.ASCII.GetBytes("isto aqui e um csv,1,2,3"));

        Assert.True(resultado.IsFailure);
        Assert.Equal("ofx.not_ofx", resultado.Error.Code);
    }

    [Fact]
    public void Recusa_arquivo_vazio()
    {
        Result<OfxNode> resultado = OfxParser.Parse(Array.Empty<byte>());

        Assert.True(resultado.IsFailure);
        Assert.Equal("ofx.empty", resultado.Error.Code);
    }

    [Fact]
    public void Recusa_ofx_sem_extrato()
    {
        const string soCabecalho = """
            <OFX>
            <SIGNONMSGSRSV1>
            <SONRS>
            <LANGUAGE>POR
            </SONRS>
            </SIGNONMSGSRSV1>
            </OFX>
            """;

        Result<OfxNode> arvore = OfxParser.Parse(Encoding.ASCII.GetBytes(soCabecalho));
        Assert.True(arvore.IsSuccess);

        Result<IReadOnlyList<OfxStatement>> extratos = OfxStatementReader.Read(arvore.Value);

        Assert.True(extratos.IsFailure);
        Assert.Equal("ofx.no_statement", extratos.Error.Code);
    }

    [Fact]
    public void Le_fatura_de_cartao_alem_de_conta_corrente()
    {
        const string cartao = """
            OFXHEADER:100
            DATA:OFXSGML
            VERSION:102

            <OFX>
            <CREDITCARDMSGSRSV1>
            <CCSTMTTRNRS>
            <CCSTMTRS>
            <CURDEF>BRL
            <CCACCTFROM>
            <ACCTID>4111********1111
            </CCACCTFROM>
            <BANKTRANLIST>
            <STMTTRN>
            <TRNTYPE>DEBIT
            <DTPOSTED>20240312000000[-03:BRT]
            <TRNAMT>-89.90
            <FITID>CC-778
            <MEMO>NETFLIX
            </STMTTRN>
            </BANKTRANLIST>
            </CCSTMTRS>
            </CCSTMTTRNRS>
            </CREDITCARDMSGSRSV1>
            </OFX>
            """;

        OfxStatement extrato = LerUnico(cartao);

        Assert.Equal("4111********1111", extrato.AccountId);
        OfxEntry linha = Assert.Single(extrato.Entries);
        Assert.Equal(-8990, linha.Amount.MinorUnits);
        Assert.Equal("NETFLIX", linha.Description);
    }

    [Fact]
    public void Le_dois_extratos_no_mesmo_arquivo()
    {
        string dois = ExtratoSgml.Replace(
            "</BANKMSGSRSV1>",
            """
            </BANKMSGSRSV1>
            <BANKMSGSRSV1>
            <STMTTRNRS>
            <STMTRS>
            <CURDEF>BRL
            <BANKACCTFROM>
            <BANKID>341
            <ACCTID>99999-9
            <ACCTTYPE>SAVINGS
            </BANKACCTFROM>
            <BANKTRANLIST>
            <STMTTRN>
            <TRNTYPE>CREDIT
            <DTPOSTED>20240120000000[-03:BRT]
            <TRNAMT>10.00
            <FITID>POUP-1
            <MEMO>RENDIMENTO
            </STMTTRN>
            </BANKTRANLIST>
            </STMTRS>
            </STMTTRNRS>
            </BANKMSGSRSV1>
            """,
            StringComparison.Ordinal);

        Result<OfxNode> arvore = OfxParser.Parse(Encoding.ASCII.GetBytes(dois));
        Assert.True(arvore.IsSuccess);

        Result<IReadOnlyList<OfxStatement>> extratos = OfxStatementReader.Read(arvore.Value);
        Assert.True(extratos.IsSuccess, $"{extratos.Error}");

        Assert.Equal(2, extratos.Value.Count);
        Assert.Equal("12345-6", extratos.Value[0].AccountId);
        Assert.Equal("99999-9", extratos.Value[1].AccountId);
    }

    [Theory]
    [InlineData("20240115", 2024, 1, 15)]
    [InlineData("20240115120000", 2024, 1, 15)]
    [InlineData("20240115120000.000", 2024, 1, 15)]
    [InlineData("20240115120000[-03:BRT]", 2024, 1, 15)]
    [InlineData("20241231235959[-03:BRT]", 2024, 12, 31)]
    [InlineData("20240229000000[0:GMT]", 2024, 2, 29)]
    public void Le_todos_os_formatos_de_data_do_ofx(string bruto, int ano, int mes, int dia)
    {
        DateOnly? data = OfxStatementReader.ParseDate(bruto);

        Assert.Equal(new DateOnly(ano, mes, dia), data);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2024")]
    [InlineData("20241301")]
    [InlineData("nao-e-data")]
    public void Recusa_data_invalida(string bruto)
    {
        Assert.Null(OfxStatementReader.ParseDate(bruto));
    }

    [Theory]
    [InlineData("-45.90", -4590)]
    [InlineData("-45,90", -4590)]
    [InlineData("45.90", 4590)]
    [InlineData("+45.90", 4590)]
    [InlineData("0.00", 0)]
    [InlineData("1234.56", 123456)]
    [InlineData("1,234.56", 123456)]
    [InlineData("-0.01", -1)]
    public void Le_valores_em_formatos_diferentes(string bruto, long esperado)
    {
        Result<Money> valor = OfxStatementReader.ParseAmount(bruto, Currency.Brl);

        Assert.True(valor.IsSuccess, $"{bruto}: {valor.Error}");
        Assert.Equal(esperado, valor.Value.MinorUnits);
    }

    /// <summary>
    /// Tres casas decimais nao existem em real. Recusar e melhor do que
    /// arredondar em silencio: se um banco mandar isso, alguem precisa
    /// olhar, nao descobrir meses depois que faltou um centavo.
    /// </summary>
    [Fact]
    public void Recusa_precisao_alem_da_moeda()
    {
        Result<Money> valor = OfxStatementReader.ParseAmount("10.005", Currency.Brl);

        Assert.True(valor.IsFailure);
    }

    private static Encoding CodificacaoLatina()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
