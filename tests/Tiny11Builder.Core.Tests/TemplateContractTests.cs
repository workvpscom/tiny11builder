using System.Xml.Linq;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateContractTests
{
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";

    // TM-04 / TM-T11: fixture hợp lệ đi qua cùng semantic boundary của build.
    [Fact]
    public void Generated_answers_satisfy_template_policy()
    {
        TemplateContractValidator.ValidateInitialAnswer(Initial());
        TemplateContractValidator.ValidateFinalAnswer(TemplateGuestAssets.FinalAnswerFile());
    }

    // TM-04 / TM-T11: negative expected từ launcher/ProductKey contract đã duyệt.
    [Theory]
    [InlineData("launcher")]
    [InlineData("key")]
    [InlineData("reboot")]
    [InlineData("audit")]
    public void Invalid_initial_answer_is_rejected(string fault)
    {
        var answer = XDocument.Parse(Initial());
        switch (fault)
        {
            case "launcher": answer.Descendants(Ns + "Path").First().Value = "powershell.exe -File wrong.ps1"; break;
            case "key": answer.Descendants(Ns + "ProductKey").Single().Add(new XElement(Ns + "Key", "not-approved")); break;
            case "reboot": answer.Descendants(Ns + "WillReboot").First().Value = "Never"; break;
            case "audit": answer.Descendants(Ns + "Mode").Single().Value = "OOBE"; break;
        }
        Assert.Throws<InvalidDataException>(() => TemplateContractValidator.ValidateInitialAnswer(answer.ToString()));
    }

    // TM-04 / TM-T11: clone không chứa credentials hoặc preparation trigger.
    [Theory]
    [InlineData("Password")]
    [InlineData("AutoLogon")]
    [InlineData("Reseal")]
    [InlineData("trigger")]
    [InlineData("extra-command")]
    [InlineData("order")]
    public void Invalid_clone_answer_is_rejected(string fault)
    {
        var answer = XDocument.Parse(TemplateGuestAssets.FinalAnswerFile());
        if (fault == "trigger") answer.Descendants(Ns + "Path").First().Value = "Prepare-Template.ps1";
        else if (fault == "extra-command") answer.Descendants(Ns + "RunSynchronous").Single().Add(
            new XElement(Ns + "RunSynchronousCommand", new XElement(Ns + "Order", 3), new XElement(Ns + "Path", "unexpected.exe")));
        else if (fault == "order") answer.Descendants(Ns + "Order").First().Value = "2";
        else answer.Root!.Add(new XElement(Ns + fault, "unexpected"));
        Assert.Throws<InvalidDataException>(() => TemplateContractValidator.ValidateFinalAnswer(answer.ToString()));
    }

    // TM-04 / TM-T12: độc lập với hash manifest; config đã ký lại vẫn sai policy.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Valid_configuration_satisfies_lifecycle_policy(bool main) =>
        TemplateContractValidator.ValidateConfiguration(main ? TemplateGuestAssets.MainConfiguration : TemplateGuestAssets.UnattendConfiguration, main);

    // TM-04 / TM-T12: allowlist bảo vệ khỏi metadata/plugin ngoài scope.
    [Theory]
    [InlineData("ProxmoxReconcilePlugin", "UnknownPlugin")]
    [InlineData("ConfigDriveService", "HttpService")]
    [InlineData("username=Administrator", "username=Other")]
    [InlineData("inject_user_password=true", "inject_user_password=false")]
    public void Configuration_outside_policy_is_rejected(string from, string to) =>
        Assert.Throws<InvalidDataException>(() => TemplateContractValidator.ValidateConfiguration(
            TemplateGuestAssets.MainConfiguration.Replace(from, to, StringComparison.Ordinal), true));

    // TM-04 / TM-T13: byte boundary độc lập với BOM-aware ReadAllText.
    [Fact]
    public void Configuration_encoding_rejects_bom()
    {
        var plain = System.Text.Encoding.UTF8.GetBytes("[DEFAULT]\nusername=Administrator\n");
        TemplatePayloadStager.ValidateConfigurationEncoding(plain);
        Assert.Throws<InvalidDataException>(() => TemplatePayloadStager.ValidateConfigurationEncoding(
            new byte[] { 0xef, 0xbb, 0xbf }.Concat(plain).ToArray()));
    }

    private static string Initial() => UnattendBuilder.Create(
        new WindowsImage(6, "Windows 11 Pro", "Pro", "x64", new Version(10, 0, 26300, 9457), "Professional", "Client", 26770709765),
        new BuildRecipe { TemplateProfile = TemplateProfile.ProxmoxCloudbaseInit });
}
