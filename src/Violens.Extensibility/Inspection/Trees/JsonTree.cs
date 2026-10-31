using System.Text.Json;

namespace Violens.Extensibility.Inspection;

/// <summary>Turns a parsed JSON document into <see cref="TreeNode"/>s, keeping numbers as written.</summary>
internal static class JsonTree
{
    public static TreeNode Build(JsonElement element, string name, TreeBudget budget)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var children = new List<TreeNode>();
                var hidden = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (hidden == 0 && budget.TryTake())
                    {
                        children.Add(Build(property.Value, property.Name, budget));
                    }
                    else
                    {
                        hidden++;
                    }
                }

                if (hidden > 0)
                {
                    children.Add(TreeBudget.Omitted(hidden));
                }

                return new TreeNode(name, TreeNodeKind.Object, "", children);
            }

            case JsonValueKind.Array:
            {
                var children = new List<TreeNode>();
                var index = 0;
                var hidden = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (hidden == 0 && budget.TryTake())
                    {
                        children.Add(Build(item, $"[{index}]", budget));
                    }
                    else
                    {
                        hidden++;
                    }

                    index++;
                }

                if (hidden > 0)
                {
                    children.Add(TreeBudget.Omitted(hidden));
                }

                return new TreeNode(name, TreeNodeKind.Array, "", children);
            }

            case JsonValueKind.String:
                return new TreeNode(name, TreeNodeKind.String, element.GetString()!);
            case JsonValueKind.Number:
                return new TreeNode(name, TreeNodeKind.Number, element.GetRawText());
            case JsonValueKind.True:
                return new TreeNode(name, TreeNodeKind.Boolean, "true");
            case JsonValueKind.False:
                return new TreeNode(name, TreeNodeKind.Boolean, "false");
            default:
                return new TreeNode(name, TreeNodeKind.Null, "null");
        }
    }
}
