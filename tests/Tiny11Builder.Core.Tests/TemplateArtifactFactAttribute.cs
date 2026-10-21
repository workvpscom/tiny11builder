using Xunit;

namespace Tiny11Builder.Core.Tests;

// AC-4: Optional local discovery skips honestly; the required verification lane executes and fails missing prerequisites.
public sealed class TemplateArtifactFactAttribute : FactAttribute
{
    public TemplateArtifactFactAttribute(string variable)
    {
        if (Environment.GetEnvironmentVariable("TINY11_REQUIRE_TEMPLATE_ARTIFACTS") != "1" &&
            MissingPrerequisite(variable) is { } reason)
            Skip = reason;
    }

    internal static string? MissingPrerequisite(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return $"Set {variable} to run template artifact verification.";
        if (!OperatingSystem.IsWindows()) return "Template artifact verification requires Windows.";
        if (variable == "TINY11_TEST_TEMPLATE_CACHE")
            return new[] { "CloudbaseInitSetup_1_1_9_x64.msi", "qemu-ga-110.2.3-x64.msi" }
                .Any(name => !File.Exists(Path.Combine(value, name))) ? $"{variable} must contain both pinned installers." : null;
        return File.Exists(value) ? null : $"{variable} must point to an existing installer.";
    }

    internal static string Require(string variable)
    {
        Assert.Null(MissingPrerequisite(variable));
        return Environment.GetEnvironmentVariable(variable)!;
    }
}
