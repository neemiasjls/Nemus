namespace Nemus.Domain.Primitives;

/// <summary>
/// Resultado sem valor. Violacao de invariante e retorno esperado, nao
/// excecao: construir uma transacao desbalanceada e um caso de uso normal
/// da UI, nao um bug.
/// </summary>
public readonly struct Result
{
    private Result(bool isSuccess, Error error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);

    public static implicit operator Result(Error error) => Failure(error);

    public override string ToString() => IsSuccess ? "Ok" : Error.ToString();
}

/// <summary>Resultado que carrega um valor em caso de sucesso.</summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, Error error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Tentativa de ler o valor de um Result que falhou ({Error}).");

    public static Result<T> Success(T value) => new(true, value, Error.None);
    public static Result<T> Failure(Error error) => new(false, default, error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Failure(error);

    public bool TryGetValue(out T value)
    {
        value = _value!;
        return IsSuccess;
    }

    public override string ToString() => IsSuccess ? $"Ok({_value})" : Error.ToString();
}
