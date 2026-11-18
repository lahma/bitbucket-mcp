using System.Reflection;

using Bitbucket.Mcp.Http;


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
    public static IEnumerable<TestCaseData> AllFieldSets =>
        Constants().Select(constant => new TestCaseData(constant.Name, (string) constant.GetRawConstantValue()!));

    [Test]
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

            Assert.That(Entries(value), Does.Contain("next"));
        }

        // Guards the rule against becoming vacuous if the paginated sets are ever renamed away.
        Assert.That(paginated, Is.Not.Empty);
    }

    [Test]
    public void NonPaginatedFieldSetsExistSoTheRuleIsDiscriminating()
    {
        var unpaginated = new List<string>();

        foreach (var constant in Constants())
        {
            var value = (string) constant.GetRawConstantValue()!;

            if (!value.Contains("values.", StringComparison.Ordinal))
            {
                unpaginated.Add(constant.Name);
                Assert.That(Entries(value), Does.Not.Contain("next"));
            }
        }

        Assert.That(unpaginated, Is.Not.Empty);
    }

    [TestCaseSource(nameof(AllFieldSets))]
    public void EveryFieldSetIsANonEmptyCommaSeparatedListWithoutWhitespace(string name, string value)
    {
        Assert.That(string.IsNullOrEmpty(value), Is.False, $"{name} is empty.");

        // Whitespace would be sent as %20 inside the query value; Bitbucket does not trim it and
        // the whole field list is then silently ignored.
        Assert.That(value.Any(char.IsWhiteSpace), Is.False, $"{name} contains whitespace.");

        Assert.That(value, Does.Not.Contain(",,"));
        Assert.That(value.StartsWith(','), Is.False, $"{name} starts with a comma.");
        Assert.That(value.EndsWith(','), Is.False, $"{name} ends with a comma.");

        foreach (var entry in Entries(value))
        {
            Assert.That(entry, Is.Not.EqualTo(string.Empty));
            Assert.That(entry.StartsWith('.'), Is.False, $"{name} contains the malformed entry '{entry}'.");
            Assert.That(entry.EndsWith('.'), Is.False, $"{name} contains the malformed entry '{entry}'.");
        }
    }

    [TestCaseSource(nameof(AllFieldSets))]
    public void NoFieldSetRepeatsAnEntry(string name, string value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in Entries(value))
        {
            Assert.That(seen.Add(entry), Is.True, $"{name} names '{entry}' more than once.");
        }
    }

    [TestCaseSource(nameof(AllFieldSets))]
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

            Assert.That(isEnvelopeKey || entry.StartsWith("values.", StringComparison.Ordinal), Is.True, $"{name} is a paginated field set but names '{entry}', which is neither an envelope key nor a values.* member.");
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
