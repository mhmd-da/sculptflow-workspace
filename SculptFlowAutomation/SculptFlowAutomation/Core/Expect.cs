using System.Text.Json.Nodes;

namespace SculptFlowAutomation.Core;

/// <summary>Small assertion helpers; each failure message says what was expected and what came back.</summary>
public static class Expect
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new TestFailure(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailure($"{what}: expected \"{expected}\", got \"{actual}\".");
        }
    }

    public static void EqualText(string expected, JsonNode? actual, string what) =>
        Equal(expected, actual?.ToString() ?? "(missing)", what);

    public static void ContainsId(JsonArray items, Guid id, string what, string idProperty = "id")
    {
        if (!items.Any(i => string.Equals(i?[idProperty]?.ToString(), id.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new TestFailure($"{what}: {id} is not in the list ({items.Count} items).");
        }
    }

    public static void LacksId(JsonArray items, Guid id, string what, string idProperty = "id")
    {
        if (items.Any(i => string.Equals(i?[idProperty]?.ToString(), id.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new TestFailure($"{what}: {id} should not be in the list.");
        }
    }
}
