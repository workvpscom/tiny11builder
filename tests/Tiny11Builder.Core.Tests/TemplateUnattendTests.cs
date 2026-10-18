using System.Xml.Linq;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateUnattendTests
{
    // PL-03: workaround A chỉ final clone answer, không credentials/Audit.
    [Fact]
    public void Clone_answer_uses_approved_oobe_workaround_without_shared_credentials()
    {
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        var answer = XDocument.Parse(TemplateGuestAssets.FinalAnswerFile());

        Assert.Equal("true", answer.Descendants(ns + "SkipMachineOOBE").Single().Value);
        Assert.Equal("true", answer.Descendants(ns + "SkipUserOOBE").Single().Value);
        Assert.Empty(answer.Descendants(ns + "Password"));
        Assert.Empty(answer.Descendants(ns + "AutoLogon"));
        Assert.Empty(answer.Descendants(ns + "UserAccounts"));
        Assert.Empty(answer.Descendants(ns + "Reseal"));
        Assert.Empty(answer.Descendants(ns + "HideEULAPage"));
        Assert.Contains(answer.Descendants(ns + "Path"), path => path.Value == "net.exe user Administrator /active:yes");
        Assert.Contains(answer.Descendants(ns + "Path"), path => path.Value == @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\Tiny11Template\Initialize-Clone.ps1");
    }

    // GP-06: Audit trigger chỉ thuộc profile và không bake credentials/disk wipe.
    [Fact]
    public void Template_answer_file_enters_audit_without_credentials_or_disk_wipe()
    {
        var image = new WindowsImage(6, "Windows 11 Pro", "Pro", "x64", new Version(10, 0, 26300, 9457),
            "Professional", "Client", 26770709765);
        var recipe = new BuildRecipe { TemplateProfile = TemplateProfile.ProxmoxCloudbaseInit };
        var document = XDocument.Parse(UnattendBuilder.Create(image, recipe));
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        var audit = document.Root!.Elements(ns + "settings").Single(e => (string?)e.Attribute("pass") == "auditUser");

        Assert.Contains(document.Descendants(ns + "Mode"), element => element.Value == "Audit");
        Assert.Equal(2, audit.Descendants(ns + "RunSynchronousCommand").Count());
        Assert.Contains(audit.Descendants(ns + "Path"), element => element.Value.EndsWith("Prepare-Template.ps1 -InvocationContext AuditUser", StringComparison.Ordinal));
        Assert.Contains(audit.Descendants(ns + "Path"), element => element.Value.EndsWith("Finalize-Template.ps1 -InvocationContext AuditUser", StringComparison.Ordinal));
        Assert.Equal("Always", document.Descendants(ns + "ProductKey").Single().Element(ns + "WillShowUI")!.Value);
        Assert.Empty(document.Descendants(ns + "ProductKey").Single().Elements(ns + "Key"));
        Assert.Empty(document.Descendants(ns + "Password"));
        Assert.Empty(document.Descendants(ns + "AutoLogon"));
        Assert.Empty(document.Descendants(ns + "DiskConfiguration"));
        Assert.DoesNotContain(document.Root.Elements(ns + "settings"), element => (string?)element.Attribute("pass") == "specialize");
    }
}
