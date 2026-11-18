namespace Bitbucket.Mcp.Tests;

/// <summary>
/// The assertions that also hand back the value they checked. NUnit's constraint model asserts
/// without returning anything, and a test that goes on to inspect the single element of a
/// collection, or a value it has just proved non-null, would otherwise need a second lookup.
/// </summary>
internal static class Expect
{
    /// <summary>Asserts that <paramref name="source"/> holds exactly one element and returns it.</summary>
    public static T Single<T>(IEnumerable<T> source)
    {
        var items = source.ToList();
        Assert.That(items, Has.Count.EqualTo(1));
        return items[0];
    }

    /// <summary>Asserts that <paramref name="code"/> throws exactly <typeparamref name="T"/> and returns it.</summary>
    /// <remarks>
    /// NUnit types the exception as nullable because inside <c>Assert.Multiple</c> a failure is
    /// deferred and the call returns null. This suite never defers, so a failure has already thrown.
    /// </remarks>
    public static T Throws<T>(Action code)
        where T : Exception => Assert.Throws<T>(code)!;

    /// <summary>The asynchronous <see cref="Throws{T}"/>.</summary>
    public static async Task<T> ThrowsAsync<T>(Func<Task> code)
        where T : Exception => (await Assert.ThrowsAsync<T>(code))!;

    /// <summary>Asserts that <paramref name="code"/> throws <typeparamref name="T"/> or a subclass of it, and returns it.</summary>
    public static async Task<T> CatchAsync<T>(Func<Task> code)
        where T : Exception => (await Assert.CatchAsync<T>(code))!;

    /// <summary>Asserts that <paramref name="value"/> is not null and returns it.</summary>
    public static T NotNull<T>(T? value)
        where T : class
    {
        Assert.That(value, Is.Not.Null);
        return value!;
    }

    /// <summary>Asserts that <paramref name="value"/> has a value and returns it.</summary>
    public static T NotNull<T>(T? value)
        where T : struct
    {
        Assert.That(value, Is.Not.Null);
        return value!.Value;
    }
}