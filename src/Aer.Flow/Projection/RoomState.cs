using Aer.Flow.Domain;

namespace Aer.Flow.Projection;

public sealed record RoomState(
    IReadOnlyDictionary<HeldWorkRef, HeldWorkState> HeldWork)
{
    public bool Equals(RoomState? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (HeldWork.Count != other.HeldWork.Count)
        {
            return false;
        }

        foreach (var (key, value) in HeldWork)
        {
            if (!other.HeldWork.TryGetValue(key, out var otherValue) || !value.Equals(otherValue))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in HeldWork.OrderBy(kv => kv.Key.Value))
        {
            hash.Add(key);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
