using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateRecipeTests
{
    // TM-01 / TM-T01: recipe cũ không tự bật template profile.
    [Fact]
    public void Legacy_recipe_without_template_field_keeps_profile_disabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tiny11-legacy-recipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "recipe.json");
            File.WriteAllText(path, "{}");

            var restored = BuildRecipe.Read(path);

            Assert.Equal(TemplateProfile.None, restored.TemplateProfile);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // TM-02 / TM-T03: enum không được hỗ trợ phải bị từ chối.
    [Fact]
    public void Unknown_template_profile_is_rejected()
    {
        var recipe = new BuildRecipe { TemplateProfile = (TemplateProfile)99 };

        Assert.Throws<InvalidDataException>(() => recipe.ValidateOptions());
    }

    // TM-02 / TM-T03: chỉ fixed Core hoặc legacy equivalent được dùng profile.
    [Theory]
    [InlineData(BuildMode.Tiny11)]
    [InlineData(BuildMode.Custom)]
    public void Template_profile_rejects_unsupported_build_modes(BuildMode mode)
    {
        var recipe = new BuildRecipe
        {
            Mode = mode,
            Flavor = mode == BuildMode.Custom ? BuildFlavor.Core : BuildFlavor.Regular,
            CoreAcknowledged = true,
            SelectedOptions = CleanupCatalog.ForMode(mode),
            TemplateProfile = TemplateProfile.ProxmoxCloudbaseInit
        };

        Assert.Throws<InvalidDataException>(() => recipe.ValidateOptions());
    }

    // TM-02 / TM-T03: legacy recipe lỗi phải trả validation error.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Legacy_template_recipe_rejects_null_arrays(bool nullActions)
    {
        var recipe = new BuildRecipe
        {
            Flavor = BuildFlavor.Core,
            CoreAcknowledged = true,
            TemplateProfile = TemplateProfile.ProxmoxCloudbaseInit,
            ExpertActions = nullActions ? null! : [],
            SelectedOptions = nullActions ? CleanupCatalog.ForMode(BuildMode.Tiny11Core) : null!
        };

        Assert.Throws<InvalidDataException>(() => recipe.ValidateOptions());
    }

    // TM-02 / TM-T03: legacy Core phải có đúng fixed removal set.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Legacy_core_template_requires_fixed_removal_set(bool fixedSet)
    {
        var recipe = new BuildRecipe
        {
            Flavor = BuildFlavor.Core, CoreAcknowledged = true,
            SelectedOptions = fixedSet ? CleanupCatalog.ForMode(BuildMode.Tiny11Core) : ["camera"],
            TemplateProfile = TemplateProfile.ProxmoxCloudbaseInit
        };

        if (fixedSet) recipe.ValidateOptions();
        else Assert.Throws<InvalidDataException>(() => recipe.ValidateOptions());
    }

    // TM-02 / TM-T03: architecture từ metadata bị reject trước export/job.
    [Fact]
    public async Task Template_profile_rejects_arm64_before_creating_a_job()
    {
        using var fixture = new BuildFixture(architecture: "arm64");
        var recipe = fixture.Recipe with
        {
            Mode = BuildMode.Tiny11Core, Flavor = BuildFlavor.Core, CoreAcknowledged = true,
            SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11Core),
            TemplateProfile = TemplateProfile.ProxmoxCloudbaseInit
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.BuildAsync(recipe, null, CancellationToken.None));
        Assert.Contains("Windows 11 x64", error.Message);
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Get-WimInfo"));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Export-Image"));
        Assert.False(Directory.Exists(recipe.WorkspacePath));
    }

    // TM-07 / TM-T09: không adopt job không tồn tại khi resume profile.
    [Fact]
    public async Task Template_profile_cannot_resume_a_missing_job()
    {
        using var fixture = new BuildFixture();
        var recipe = fixture.Recipe with { TemplateProfile = TemplateProfile.ProxmoxCloudbaseInit };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.ResumeAsync(
            Path.Combine(fixture.Root, "missing-job.json"), recipe, null, CancellationToken.None));
        Assert.Empty(fixture.Runner.Calls);
    }

    // TM-02 / TM-T02: public recipe IO giữ profile đã chọn.
    [Fact]
    public void Template_profile_survives_recipe_round_trip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tiny11-template-recipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "recipe.json");
            var recipe = new BuildRecipe
            {
                Mode = BuildMode.Tiny11Core,
                Flavor = BuildFlavor.Core,
                CoreAcknowledged = true,
                SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11Core),
                TemplateProfile = TemplateProfile.ProxmoxCloudbaseInit
            };

            recipe.Write(path);
            var restored = BuildRecipe.Read(path);

            Assert.Equal(TemplateProfile.ProxmoxCloudbaseInit, restored.TemplateProfile);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
