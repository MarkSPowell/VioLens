namespace Violens.Extensibility.Inspection;

/// <summary>
/// Caps how many nodes one tree holds, so a huge body doesn't build a tree
/// too big to show. Readers take a node from the budget before keeping
/// one; once it's spent they still read the rest (a binary format has to be
/// walked to find where the next value starts) but keep none of it, and
/// each container ends with an <see cref="TreeNodeKind.Omitted"/> marker.
/// </summary>
internal sealed class TreeBudget(int maxNodes)
{
    public const int DefaultMaxNodes = 10_000;

    public int Shown { get; private set; }

    public bool Truncated { get; private set; }

    public bool TryTake()
    {
        if (Shown >= maxNodes)
        {
            Truncated = true;
            return false;
        }

        Shown++;
        return true;
    }

    public static TreeNode Omitted(int count) =>
        new("", TreeNodeKind.Omitted, count == 1 ? "1 more not shown" : $"{count:N0} more not shown");
}
