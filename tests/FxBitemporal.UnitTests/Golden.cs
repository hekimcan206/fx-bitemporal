namespace FxBitemporal.UnitTests;

/// <summary>
/// Golden files live in tests/FxBitemporal.UnitTests/Golden and are compared byte for byte. Set UPDATE_GOLDEN=1 to
/// rewrite them from the current output, then review the diff like any other code change.
/// </summary>
internal static class Golden
{
    public static void Verify(string name, string actual)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", name));
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, actual);
            return;
        }

        File.Exists(path).Should().BeTrue($"golden file {name} must exist (run once with UPDATE_GOLDEN=1)");
        File.ReadAllText(path).ReplaceLineEndings("\n").Should().Be(actual);
    }
}
