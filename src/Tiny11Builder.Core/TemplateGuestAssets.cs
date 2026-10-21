using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Tiny11Builder.Core;

internal static class TemplateGuestAssets
{
    internal static string PrepareScript => Read("Prepare-Template.ps1");
    internal static string FinalizeScript => Read("Finalize-Template.ps1");
    internal static string CloneScript => Read("Initialize-Clone.ps1");
    internal static string ReconcilePlugin => Read("tiny11_cloudbase.py");
    internal static string PolicyScript => Read("Configure-GuestPolicy.ps1");
    internal static string TransformScript => Read("Create-DisabledServiceTransform.ps1");
    internal static string StateScript => Read("Template-State.ps1");

    internal static byte[] Driver(string name)
    {
        using var stream = typeof(TemplateGuestAssets).Assembly.GetManifestResourceStream("Tiny11Builder.Core.Template.Drivers." + name)
            ?? throw new InvalidOperationException("Template driver resource is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string Read(string name)
    {
        using var stream = typeof(TemplateGuestAssets).Assembly.GetManifestResourceStream("Tiny11Builder.Core.Template." + name)
            ?? throw new InvalidOperationException("Template guest resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal const string MainConfiguration = """
        [DEFAULT]
        username=Administrator
        inject_user_password=true
        first_logon_behaviour=no
        rename_admin_user=false
        metadata_services=cloudbaseinit.metadata.services.configdrive.ConfigDriveService
        plugins=tiny11_cloudbase.ProxmoxReconcilePlugin,cloudbaseinit.plugins.windows.extendvolumes.ExtendVolumesPlugin
        allow_reboot=true
        stop_service_on_exit=true
        process_userdata=false
        cloud_config_plugins=hostname
        logdir=C:\ProgramData\Tiny11Template\logs
        logfile=cloudbase-init.log
        debug=false
        """;

    internal const string UnattendConfiguration = """
        [DEFAULT]
        username=Administrator
        metadata_services=cloudbaseinit.metadata.services.configdrive.ConfigDriveService
        plugins=tiny11_cloudbase.ProxmoxReconcilePlugin
        allow_reboot=false
        stop_service_on_exit=false
        logdir=C:\ProgramData\Tiny11Template\logs
        logfile=cloudbase-init-unattend.log
        debug=false
        """;

    internal static string FinalAnswerFile()
    {
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        XNamespace wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
        XElement Command(int order, string path) => new(ns + "RunSynchronousCommand",
            new XAttribute(wcm + "action", "add"), new XElement(ns + "Order", order),
            new XElement(ns + "Path", path));
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", wcm),
                new XElement(ns + "settings", new XAttribute("pass", "specialize"),
                    new XElement(ns + "component", new XAttribute("name", "Microsoft-Windows-Deployment"),
                        new XAttribute("processorArchitecture", "amd64"),
                        new XAttribute("publicKeyToken", "31bf3856ad364e35"),
                        new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"),
                        new XElement(ns + "RunSynchronous",
                            Command(1, @"net.exe user Administrator /active:yes"),
                            Command(2, @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\Tiny11Template\Initialize-Clone.ps1")))),
                new XElement(ns + "settings", new XAttribute("pass", "oobeSystem"),
                    new XElement(ns + "component", new XAttribute("name", "Microsoft-Windows-Shell-Setup"),
                        new XAttribute("processorArchitecture", "amd64"),
                        new XAttribute("publicKeyToken", "31bf3856ad364e35"),
                        new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"),
                        new XElement(ns + "OOBE", new XElement(ns + "SkipMachineOOBE", true),
                            new XElement(ns + "SkipUserOOBE", true)))))).ToString();
    }
}
