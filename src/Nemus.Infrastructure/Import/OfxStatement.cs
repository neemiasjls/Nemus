using System.Globalization;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;

namespace Nemus.Infrastructure.Import;

/// <summary>Uma linha do extrato, ainda crua - do jeito que o banco mandou.</summary>
public sealed record OfxEntry(
    string FitId,
    DateOnly PostedOn,
    Money Amount,
    string? Type,
    string? Memo,
    string? Name,
    string? CheckNumber)
{
    /// <summary>
    /// A descricao util. Bancos brasileiros divergem: alguns preenchem
    /// NAME, outros so MEMO, outros os dois com textos diferentes. Preferir
    /// MEMO porque costuma trazer o estabelecimento; NAME as vezes traz so
    /// "COMPRA CARTAO".
    /// </summary>
    public string Description =>
        !string.IsNullOrWhiteSpace(Memo) ? Memo.Trim()
        : !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
        : "Lancamento importado";
}

/// <summary>Um extrato: uma conta, um periodo, N linhas.</summary>
public sealed record OfxStatement(
    string? BankId,
    string? AccountId,
    string? AccountType,
    Currency Currency,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    Money? ClosingBalance,
    IReadOnlyList<OfxEntry> Entries);

public static class OfxStatementReader
{
    /// <summary>
    /// Extrai os extratos da arvore. Um arquivo OFX pode trazer mais de um
    /// (conta corrente e cartao no mesmo download), e cada um tem sua
    /// propria ACCTID - que faz parte da chave de idempotencia.
    /// </summary>
    public static Result<IReadOnlyList<OfxStatement>> Read(OfxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var statements = new List<OfxStatement>();

        // STMTRS = conta bancaria. CCSTMTRS = cartao de credito.
        foreach (OfxNode block in root.Descendants("STMTRS").Concat(root.Descendants("CCSTMTRS")))
        {
            Result<OfxStatement> statement = ReadStatement(block);
            if (statement.IsFailure)
            {
                return statement.Error;
            }

            statements.Add(statement.Value);
        }

        if (statements.Count == 0)
        {
            return new Error(
                "ofx.no_statement",
                "O arquivo nao tem extrato: nenhum bloco STMTRS ou CCSTMTRS foi encontrado.");
        }

        return statements;
    }

    private static Result<OfxStatement> ReadStatement(OfxNode block)
    {
        string currencyCode = block.ChildValue("CURDEF") ?? "BRL";
        Result<Currency> currency = Currency.TryFrom(currencyCode);
        if (currency.IsFailure)
        {
            return currency.Error;
        }

        OfxNode? account = block.Child("BANKACCTFROM") ?? block.Child("CCACCTFROM");
        OfxNode? list = block.Child("BANKTRANLIST") ?? block.Child("CCTRANLIST");

        var entries = new List<OfxEntry>();

        foreach (OfxNode stmttrn in (list ?? block).Descendants("STMTTRN"))
        {
            Result<OfxEntry> entry = ReadEntry(stmttrn, currency.Value);
            if (entry.IsFailure)
            {
                return entry.Error;
            }

            entries.Add(entry.Value);
        }

        Money? closingBalance = null;
        OfxNode? ledger = block.Child("LEDGERBAL");
        if (ledger?.ChildValue("BALAMT") is string rawBalance
            && ParseAmount(rawBalance, currency.Value) is { IsSuccess: true } parsedBalance)
        {
            closingBalance = parsedBalance.Value;
        }

        return new OfxStatement(
            account?.ChildValue("BANKID"),
            account?.ChildValue("ACCTID"),
            account?.ChildValue("ACCTTYPE"),
            currency.Value,
            ParseDate(list?.ChildValue("DTSTART")),
            ParseDate(list?.ChildValue("DTEND")),
            closingBalance,
            entries);
    }

    private static Result<OfxEntry> ReadEntry(OfxNode stmttrn, Currency currency)
    {
        string? fitId = stmttrn.ChildValue("FITID");
        if (string.IsNullOrWhiteSpace(fitId))
        {
            return new Error(
                "ofx.missing_fitid",
                "Ha lancamento sem FITID. Sem identidade estavel a reimportacao duplicaria a linha.");
        }

        string? rawAmount = stmttrn.ChildValue("TRNAMT");
        if (string.IsNullOrWhiteSpace(rawAmount))
        {
            return new Error("ofx.missing_amount", $"Lancamento {fitId} nao tem valor (TRNAMT).");
        }

        Result<Money> amount = ParseAmount(rawAmount, currency);
        if (amount.IsFailure)
        {
            return amount.Error;
        }

        DateOnly? postedOn = ParseDate(stmttrn.ChildValue("DTPOSTED"));
        if (postedOn is null)
        {
            return new Error("ofx.missing_date", $"Lancamento {fitId} nao tem data valida (DTPOSTED).");
        }

        return new OfxEntry(
            fitId.Trim(),
            postedOn.Value,
            amount.Value,
            stmttrn.ChildValue("TRNTYPE"),
            stmttrn.ChildValue("MEMO"),
            stmttrn.ChildValue("NAME"),
            stmttrn.ChildValue("CHECKNUM"));
    }

    /// <summary>
    /// Valor monetario, sempre por caminho inteiro.
    ///
    /// A especificacao manda ponto como separador decimal, mas parte dos
    /// bancos brasileiros emite virgula. Aceitar os dois e barato; adivinhar
    /// errado custa uma ordem de grandeza no extrato. A deteccao usa o
    /// ULTIMO separador, que e o decimal em qualquer uma das convencoes.
    ///
    /// Nunca passa por double: Money.Parse acumula digito a digito em long.
    /// </summary>
    internal static Result<Money> ParseAmount(string raw, Currency currency)
    {
        string text = raw.Trim();

        if (text.Length == 0)
        {
            return new Error("ofx.amount_empty", "Valor vazio.");
        }

        int lastComma = text.LastIndexOf(',');
        int lastDot = text.LastIndexOf('.');

        return lastComma > lastDot
            ? Money.ParsePtBr(text, currency)
            : Money.ParseInvariant(text.Replace(",", string.Empty, StringComparison.Ordinal), currency);
    }

    /// <summary>
    /// Data do OFX: YYYYMMDD[HHMMSS[.XXX]][[+/-TZ:NOME]].
    ///
    /// DECISAO QUE IMPORTA: fica a data LOCAL como o banco escreveu, sem
    /// converter para UTC.
    ///
    /// Converter parece mais correto e e pior aqui. Uma compra as 21h do dia
    /// 31 em BRT (-03) vira dia 1o do mes seguinte em UTC - e occurred_on e
    /// justamente a competencia que o orcamento da fase 4 usa. A conversao
    /// jogaria a despesa para o mes errado exatamente na virada, que e onde
    /// mais doi. A data que o banco escreveu e a data que aparece no extrato
    /// da pessoa; e essa que tem que valer.
    /// </summary>
    internal static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        ReadOnlySpan<char> text = raw.AsSpan().Trim();

        // Descarta o sufixo de fuso: [-03:BRT]
        int bracket = text.IndexOf('[');
        if (bracket >= 0)
        {
            text = text[..bracket];
        }

        if (text.Length < 8)
        {
            return null;
        }

        ReadOnlySpan<char> yyyymmdd = text[..8];

        return DateOnly.TryParseExact(
            yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : null;
    }
}
