using System.Xml;
using System.Xml.Linq;

namespace Tiny11Builder.Core;

internal static class TemplateContractValidator
{
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private const string ConfigDrive = "cloudbaseinit.metadata.services.configdrive.ConfigDriveService";
    private const string Reconcile = "tiny11_cloudbase.ProxmoxReconcilePlugin";

    internal static void ValidateInitialAnswer(string xml)
    {
        var answer = Parse(xml);
        Reject(answer, "Password", "AutoLogon", "UserAccounts", "DiskConfiguration", "DiskWipe", "HideEULAPage");
        var setup = Pass(answer, "windowsPE");
        var product = setup.Descendants(Ns + "ProductKey").SingleOrDefault();
        if (product is null || product.Elements(Ns + "Key").Any() || Value(product, "WillShowUI") != "Always")
            throw new InvalidDataException("Template ProductKey UI policy is invalid.");
        if (Pass(answer, "oobeSystem").Descendants(Ns + "Reseal").SingleOrDefault()?.Element(Ns + "Mode")?.Value != "Audit")
            throw new InvalidDataException("Template initial answer must enter Audit.");
        var commands = Pass(answer, "auditUser").Descendants(Ns + "RunSynchronousCommand").ToArray();
        string[] scripts = ["Prepare-Template.ps1", "Finalize-Template.ps1"];
        if (commands.Length != 2) throw new InvalidDataException("Template Audit commands are invalid.");
        for (var index = 0; index < commands.Length; index++)
            if (Value(commands[index], "Order") != (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                Value(commands[index], "WillReboot") != "OnRequest" ||
                Value(commands[index], "Path") != "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\ProgramData\\Tiny11Template\\" + scripts[index] + " -InvocationContext AuditUser")
                throw new InvalidDataException("Template Audit launcher contract is invalid.");
        if (answer.Descendants(Ns + "SkipMachineOOBE").Any() || answer.Descendants(Ns + "SkipUserOOBE").Any() ||
            answer.Root!.Elements(Ns + "settings").Any(s => (string?)s.Attribute("pass") == "specialize"))
            throw new InvalidDataException("Clone-only settings found in initial answer.");
    }

    internal static void ValidateFinalAnswer(string xml)
    {
        var answer = Parse(xml);
        Reject(answer, "Password", "AutoLogon", "UserAccounts", "Reseal", "HideEULAPage", "Credentials");
        var oobe = Pass(answer, "oobeSystem");
        if (Value(oobe, "SkipMachineOOBE") != "true" || Value(oobe, "SkipUserOOBE") != "true" ||
            answer.Root!.Elements(Ns + "settings").Any(s => ((string?)s.Attribute("pass")) is "auditUser" or "auditSystem") ||
            answer.Descendants(Ns + "Path").Any(p => p.Value.Contains("Prepare-Template", StringComparison.OrdinalIgnoreCase) ||
                p.Value.Contains("Finalize-Template", StringComparison.OrdinalIgnoreCase) ||
                p.Value.Contains("InvocationContext", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Clone answer lifecycle policy is invalid.");
        var commands = Pass(answer, "specialize").Descendants(Ns + "RunSynchronousCommand").ToArray();
        string[] expected = ["net.exe user Administrator /active:yes",
            @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\Tiny11Template\Initialize-Clone.ps1"];
        if (commands.Length != expected.Length || commands.Where((command, index) =>
                Value(command, "Order") != (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                Value(command, "Path") != expected[index]).Any())
            throw new InvalidDataException("Clone command allowlist/order is invalid.");
    }

    internal static void ValidateConfiguration(string text, bool main)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inDefault = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                if (line != "[DEFAULT]" || inDefault) throw new InvalidDataException("Unexpected template configuration section.");
                inDefault = true;
                continue;
            }
            var separator = line.IndexOf('=');
            if (!inDefault || separator <= 0 || !values.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim()))
                throw new InvalidDataException("Malformed or duplicate template configuration option.");
        }
        void Require(string key, string expected)
        {
            if (!values.TryGetValue(key, out var value) || value != expected)
                throw new InvalidDataException("Template configuration policy mismatch: " + key);
        }
        Require("username", "Administrator");
        Require("metadata_services", ConfigDrive);
        Require("allow_reboot", main ? "true" : "false");
        Require("stop_service_on_exit", main ? "true" : "false");
        Require("plugins", main ? Reconcile + ",cloudbaseinit.plugins.windows.extendvolumes.ExtendVolumesPlugin" : Reconcile);
        if (main)
        {
            Require("inject_user_password", "true");
            Require("rename_admin_user", "false");
            Require("first_logon_behaviour", "no");
            Require("process_userdata", "false");
            Require("cloud_config_plugins", "hostname");
        }
    }

    private static XDocument Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var answer = XDocument.Load(reader);
        if (answer.Root?.Name != Ns + "unattend") throw new InvalidDataException("Unexpected template answer root.");
        return answer;
    }

    private static XElement Pass(XDocument answer, string pass)
    {
        var settings = answer.Root!.Elements(Ns + "settings").Where(s => (string?)s.Attribute("pass") == pass).ToArray();
        if (settings.Length != 1) throw new InvalidDataException("Missing or duplicate template configuration pass: " + pass);
        return settings[0];
    }

    private static string? Value(XElement element, string name)
    {
        var values = element.Descendants(Ns + name).ToArray();
        return values.Length == 1 ? values[0].Value : null;
    }

    private static void Reject(XDocument answer, params string[] names)
    {
        if (names.Any(name => answer.Descendants(Ns + name).Any()))
            throw new InvalidDataException("Forbidden credentials or setup policy in template answer.");
    }
}
