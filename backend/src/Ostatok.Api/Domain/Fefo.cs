namespace Ostatok.Api.Domain;

public sealed record Allocation(Guid BatchId, Guid ProductId, decimal Quantity);

public sealed record Shortage(Guid ProductId, decimal Required, decimal Available);

public sealed record AllocationResult(IReadOnlyList<Allocation> Allocations, IReadOnlyList<Shortage> Shortages)
{
    public bool Ok => Shortages.Count == 0;
}

/// <summary>Списание по правилу «первым истекает — первым уходит».</summary>
public static class Fefo
{
    /// <summary>
    /// Распределяет потребность по годным партиям, начиная с самого раннего срока.
    /// Если хотя бы одного продукта не хватает, не списывается ничего: возвращаются только нехватки.
    /// </summary>
    public static AllocationResult Allocate(
        IEnumerable<Batch> batches,
        IReadOnlyDictionary<Guid, decimal> required,
        DateTimeOffset now)
    {
        var fresh = batches
            .Where(b => b.Remaining > 0 && b.ExpiresAt > now)
            .ToLookup(b => b.ProductId);

        var allocations = new List<Allocation>();
        var shortages = new List<Shortage>();

        foreach (var (productId, need) in required)
        {
            var available = fresh[productId].Sum(b => b.Remaining);
            if (available < need)
            {
                shortages.Add(new Shortage(productId, need, available));
                continue;
            }

            var left = need;
            foreach (var batch in fresh[productId].OrderBy(b => b.ExpiresAt).ThenBy(b => b.Id))
            {
                if (left == 0) break;
                var take = Math.Min(left, batch.Remaining);
                allocations.Add(new Allocation(batch.Id, productId, take));
                left -= take;
            }
        }

        return shortages.Count > 0
            ? new AllocationResult([], shortages)
            : new AllocationResult(allocations, []);
    }
}
