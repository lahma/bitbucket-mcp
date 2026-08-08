using System.Reflection;

using Bitbucket.Mcp.Http;

using Xunit;

namespace Bitbucket.Mcp.Tests.Http;

/// <summary>
/// Enforces the <c>fields=</c> rules over <see cref="FieldSets"/> by reflection rather than by
/// naming each constant, so a field set added later is covered the moment it is written.
/// </summary>
/// <remarks>
/// The load-bearing one is <see cref="EveryPaginatedFieldSetRequestsNext"/> (plan risk R5). An
/// inclusive <c>fields=</c> list returns only what it names: leave <c>next</c> out of a paginated
/// set and Bitbucket answers page one with no next link, the client concludes there are no more
/// pages, and pagination is silently truncated. That is invisible against a small repository and
/// wrong against a real one, which is why it is a machine-checked rule and not a comment.
/// </remarks>
public class FieldSetTests
{
    /// <summary>The name and value of every string constant on <see cref="FieldSets"/>.</summary>
    public static TheoryData<string, string> AllFieldSets
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var constant in Constants())
            {
                data.Add(constant.Name, (string) constant.GetRawConstantValue()!);
            }

            return data;
        }
    }

    [Fact]
    public void EveryPaginatedFieldSetRequestsNext()
    {
        var paginated = new List<string>();

        foreach (var constant in Constants())
        {
            var value = (string) constant.GetRawConstantValue()!;

            // A field set that names `values.` describes a paginated envelope, and that is the
            // whole test: the shape of the value decides, so no future field set can opt out by
            // being forgotten here.
            if (!value.Contains("values.", StringComparison.Ordinal))
            {
                continue;
            }

            paginated.Add(constant.Name);

            Assert.Contains(
                "next",
                Entries(value));
        }

        // Guards the rule against becoming vacuous if the paginated sets are ever renamed away.
        Assert.NotEmpty(paginated);
    }

    [Fact]
    public void NonPaginatedFieldSetsExistSoTheRuleIsDiscriminating()
    {
        var unpaginated = new List<string>();

        foreach (var constant in Constants())
        {
            var value = (string) constant.GetRawConstantValue()!;

            if (!value.Contains("values.", StringComparison.Ordinal))
            {
                unpaginated.Add(constant.Name);
                Assert.DoesNotContain("next", Entries(value));
            }
        }

        Assert.NotEmpty(unpaginated);
    }

    [Theory]
    [MemberData(nameof(AllFieldSets))]
    public void EveryFieldSetIsANonEmptyCommaSeparatedListWithoutWhitespace(string name, string value)
    {
        Assert.False(string.IsNullOrEmpty(value), $"{name} is empty.");

        // Whitespace would be sent as %20 inside the query value; Bitbucket does not trim it and
        // the whole field list is then silently ignored.
        Assert.False(value.Any(char.IsWhiteSpace), $"{name} contains whitespace.");

        Assert.DoesNotContain(",,", value, StringComparison.Ordinal);
        Assert.False(value.StartsWith(','), $"{name} starts with a comma.");
        Assert.False(value.EndsWith(','), $"{name} ends with a comma.");

        foreach (var entry in Entries(value))
        {
            Assert.NotEqual(string.Empty, entry);
            Assert.False(entry.StartsWith('.'), $"{name} contains the malformed entry '{entry}'.");
            Assert.False(entry.EndsWith('.'), $"{name} contains the malformed entry '{entry}'.");
        }
    }

    [Theory]
    [MemberData(nameof(AllFieldSets))]
    public void NoFieldSetRepeatsAnEntry(string name, string value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in Entries(value))
        {
            Assert.True(seen.Add(entry), $"{name} names '{entry}' more than once.");
        }
    }

    [Theory]
    [MemberData(nameof(AllFieldSets))]
    public void PaginatedFieldSetsOnlyNameEnvelopeKeysAndValuesMembers(string name, string value)
    {
        var entries = Entries(value);

        if (!value.Contains("values.", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var entry in entries)
        {
            var isEnvelopeKey = entry is "next" or "size" or "page" or "pagelen";

            Assert.True(
                isEnvelopeKey || entry.StartsWith("values.", StringComparison.Ordinal),
                $"{name} is a paginated field set but names '{entry}', which is neither an envelope key nor a values.* member.");
        }
    }

    private static string[] Entries(string value) => value.Split(',');

    private static IEnumerable<FieldInfo> Constants()
    {
        foreach (var candidate in typeof(FieldSets).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (candidate.IsLiteral && !candidate.IsInitOnly && candidate.FieldType == typeof(string))
            {
                yield return candidate;
            }
        }
    }
}
