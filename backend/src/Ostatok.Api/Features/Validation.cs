namespace Ostatok.Api.Features;

public static class Validation
{
    /// <summary>Наименьшее количество: в базе три знака после запятой, меньшее значение превратилось бы в ноль.</summary>
    public const decimal MinQuantity = 0.001m;
}

/// <summary>Сбор ошибок входных данных в формате, который отдаёт ValidationProblem.</summary>
public sealed class Errors
{
    private readonly Dictionary<string, List<string>> _items = [];

    public bool Any => _items.Count > 0;

    public Errors Add(string field, string message)
    {
        if (!_items.TryGetValue(field, out var list)) _items[field] = list = [];
        list.Add(message);
        return this;
    }

    public Errors Check(bool ok, string field, string message) => ok ? this : Add(field, message);

    public IResult ToResult() =>
        Results.ValidationProblem(_items.ToDictionary(p => p.Key, p => p.Value.ToArray()));
}
