namespace LeBot.Domain.Common;

/// <summary>
/// Discriminated union over a successful value of type <typeparamref name="TValue"/>
/// or a failure of type <typeparamref name="TError"/>. Use pattern matching on
/// <see cref="Ok"/> and <see cref="Err"/>, or call <see cref="Match{TOut}"/>.
/// </summary>
public abstract record Result<TValue, TError>
{
    /// <summary>The "value present" variant.</summary>
    public sealed record Ok(TValue Value) : Result<TValue, TError>;

    /// <summary>The "error present" variant.</summary>
    public sealed record Err(TError Error) : Result<TValue, TError>;

    /// <summary>True when this result carries a successful value.</summary>
    public bool IsSuccess => this is Ok;

    /// <summary>True when this result carries an error.</summary>
    public bool IsFailure => this is Err;

    /// <summary>Folds the result into a single value by handling both variants.</summary>
    public TOut Match<TOut>(Func<TValue, TOut> onOk, Func<TError, TOut> onErr) => this switch
    {
        Ok o => onOk(o.Value),
        Err e => onErr(e.Error),
        _ => throw new InvalidOperationException("Unreachable: Result is sealed to Ok and Err."),
    };

    /// <summary>Builds a successful <see cref="Ok"/> result.</summary>
    public static Result<TValue, TError> Success(TValue value) => new Ok(value);

    /// <summary>Builds a failed <see cref="Err"/> result.</summary>
    public static Result<TValue, TError> Failure(TError error) => new Err(error);
}
