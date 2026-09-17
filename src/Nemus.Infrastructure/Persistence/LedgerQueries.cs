using Npgsql;
using NpgsqlTypes;

namespace Nemus.Infrastructure.Persistence;

public sealed record EntryView(
    Guid Id,
    Guid AccountId,
    string AccountName,
    string AccountTypeCode,
    bool AccountIsInternal,
    long Amount,
    Guid? CategoryId,
    string? CategoryName,
    string? Memo);

public sealed record TransactionView(
    Guid Id,
    DateOnly OccurredOn,
    string Description,
    string CurrencyCode,
    string Kind,
    string Source,
    string? Notes,
    DateTimeOffset CreatedAt,
    IReadOnlyList<EntryView> Entries);

public sealed record NetWorthRow(
    string CurrencyCode,
    long Assets,
    long Liabilities,
    long NetWorth);

public sealed record TransactionPage(
    IReadOnlyList<TransactionView> Items,
    int Total);

/// <summary>O que aconteceu ao tentar categorizar um lancamento existente.</summary>
public enum CategorizeOutcome
{
    Ok = 0,

    /// <summary>Nao existe, ou foi apagado.</summary>
    NotFound = 1,

    /// <summary>Transferencia entre contas proprias: nao ha perna externa para receber categoria.</summary>
    NoExternalLeg = 2,

    /// <summary>Lancamento dividido em varias categorias: cada perna tem a sua.</summary>
    Split = 3,
}

/// <summary>
/// Lado de leitura. Devolve modelo achatado em vez de reidratar o agregado:
/// a tela quer nome de conta e de categoria, que o agregado nao carrega, e
/// nao precisa das invariantes revalidadas a cada linha exibida.
///
/// Toda soma tem ::BIGINT explicito. No PostgreSQL SUM(bigint) devolve
/// NUMERIC, e sem o cast o driver entrega decimal - o pilar 2 vazaria
/// justamente aqui, na borda de leitura.
/// </summary>
public sealed class LedgerQueries
{
    /// <summary>Teto duro de pagina. Cliente nao escolhe pedir o razao inteiro.</summary>
    public const int MaxPageSize = 200;

    private readonly NpgsqlDataSource _dataSource;

    public LedgerQueries(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<TransactionPage> ListAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? accountId = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        offset = Math.Max(offset, 0);

        // Os filtros entram como parametro e sao neutralizados com IS NULL
        // dentro do proprio SQL. Nenhum pedaco de condicao e concatenado.
        const string filter = """
            WHERE t.deleted_at IS NULL
              AND (@from IS NULL OR t.occurred_on >= @from)
              AND (@to   IS NULL OR t.occurred_on <= @to)
              AND (@account_id IS NULL OR EXISTS (
                    SELECT 1 FROM entries e2
                     WHERE e2.transaction_id = t.id AND e2.account_id = @account_id))
            """;

        int total;
        await using (NpgsqlCommand counter = _dataSource.CreateCommand(
            $"SELECT COUNT(*)::BIGINT FROM transactions t {filter}"))
        {
            AddFilters(counter, from, to, accountId);
            object? scalar = await counter.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            total = scalar is long value ? (int)value : 0;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand($"""
            WITH page AS (
                SELECT t.id, t.occurred_on, t.description, t.currency_code,
                       t.kind, t.source, t.notes, t.created_at
                  FROM transactions t
                  {filter}
                 ORDER BY t.occurred_on DESC, t.created_at DESC, t.id
                 LIMIT @limit OFFSET @offset
            )
            SELECT p.id, p.occurred_on, p.description, p.currency_code,
                   p.kind, p.source, p.notes, p.created_at,
                   e.id, e.account_id, a.name, a.type_code, ty.is_internal,
                   e.amount, e.category_id, c.name, e.memo
              FROM page p
              JOIN entries e       ON e.transaction_id = p.id
              JOIN accounts a      ON a.id = e.account_id
              JOIN account_types ty ON ty.code = a.type_code
              LEFT JOIN categories c ON c.id = e.category_id
             ORDER BY p.occurred_on DESC, p.created_at DESC, p.id, e.sort_order
            """);

        AddFilters(command, from, to, accountId);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);

        var ordered = new List<TransactionView>();
        var entriesById = new Dictionary<Guid, List<EntryView>>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid txId = reader.GetGuid(0);

            if (!entriesById.TryGetValue(txId, out List<EntryView>? legs))
            {
                legs = [];
                entriesById[txId] = legs;

                ordered.Add(new TransactionView(
                    txId,
                    DateOnly.FromDateTime(reader.GetDateTime(1)),
                    reader.GetString(2),
                    reader.GetString(3).Trim(),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    legs));
            }

            legs.Add(new EntryView(
                reader.GetGuid(8),
                reader.GetGuid(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetBoolean(12),
                reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetGuid(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16)));
        }

        return new TransactionPage(ordered, total);
    }

    /// <summary>Patrimonio liquido por moeda, lido de v_net_worth.</summary>
    public async Task<IReadOnlyList<NetWorthRow>> ReadNetWorthAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = new List<NetWorthRow>();

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "SELECT currency_code, assets, liabilities, net_worth FROM v_net_worth ORDER BY currency_code");

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new NetWorthRow(
                reader.GetString(0).Trim(),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }

    /// <summary>Marca como excluida sem apagar. Devolve false se ja nao existia.</summary>
    public async Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            UPDATE transactions
               SET deleted_at = now(), updated_at = now()
             WHERE id = @id AND deleted_at IS NULL
            """);
        command.Parameters.AddWithValue("id", id);

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    /// <summary>
    /// Poe (ou tira) a categoria de um lancamento que ja existe.
    ///
    /// A categoria vai na perna da conta EXTERNA, que e onde o orcamento a
    /// procura. Por isso o lancamento precisa ter exatamente uma perna
    /// externa: transferencia entre contas proprias nao tem nenhuma, e
    /// lancamento dividido tem varias - nesse caso escolher por quem manda
    /// seria inventar para onde o dinheiro foi.
    ///
    /// Categoria e dimensao, nao dinheiro: mexer nela nao pode desbalancear
    /// nada, e por isso este caminho nao passa pelo agregado.
    /// </summary>
    public async Task<CategorizeOutcome> CategorizeAsync(
        Guid transactionId, Guid? categoryId, CancellationToken cancellationToken = default)
    {
        var externalLegs = new List<Guid>();

        await using (NpgsqlCommand legs = _dataSource.CreateCommand("""
            SELECT e.id
              FROM entries e
              JOIN transactions t   ON t.id = e.transaction_id
              JOIN accounts a       ON a.id = e.account_id
              JOIN account_types ty ON ty.code = a.type_code
             WHERE e.transaction_id = @id
               AND t.deleted_at IS NULL
               AND NOT ty.is_internal
            """))
        {
            legs.Parameters.AddWithValue("id", transactionId);

            await using NpgsqlDataReader reader =
                await legs.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                externalLegs.Add(reader.GetGuid(0));
            }
        }

        if (externalLegs.Count == 0)
        {
            // Pode ser transacao inexistente, apagada, ou transferencia. A
            // primeira e a segunda o chamador distingue consultando; a
            // terceira e a unica que interessa explicar a quem usa.
            return await ExistsAsync(transactionId, cancellationToken).ConfigureAwait(false)
                ? CategorizeOutcome.NoExternalLeg
                : CategorizeOutcome.NotFound;
        }

        if (externalLegs.Count > 1)
        {
            return CategorizeOutcome.Split;
        }

        await using NpgsqlCommand update = _dataSource.CreateCommand("""
            UPDATE entries SET category_id = @category WHERE id = @entry;
            UPDATE transactions SET updated_at = now() WHERE id = @transaction;
            """);

        update.Parameters.Add(new NpgsqlParameter("category", NpgsqlDbType.Uuid)
        {
            Value = categoryId is null ? DBNull.Value : categoryId.Value,
        });
        update.Parameters.AddWithValue("entry", externalLegs[0]);
        update.Parameters.AddWithValue("transaction", transactionId);

        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return CategorizeOutcome.Ok;
    }

    private async Task<bool> ExistsAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "SELECT 1 FROM transactions WHERE id = @id AND deleted_at IS NULL");
        command.Parameters.AddWithValue("id", transactionId);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static void AddFilters(
        NpgsqlCommand command, DateOnly? from, DateOnly? to, Guid? accountId)
    {
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date)
        {
            Value = from is null ? DBNull.Value : from.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date)
        {
            Value = to is null ? DBNull.Value : to.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("account_id", NpgsqlDbType.Uuid)
        {
            Value = accountId is null ? DBNull.Value : accountId.Value,
        });
    }
}
