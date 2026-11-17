using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 「Core が WPF / Windows UI 系を参照しない」ことの検査(ADR 0002。3 段の担保のうち、2 段目と 3 段目)。
/// 1 段目(TFM が net10.0 なのでコンパイルが通らない)は、手作業で確かめる。
/// </summary>
public sealed class CoreDependencyTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Windows.Forms",
        "System.Drawing",
        "System.Windows.Presentation",
        "UIAutomationClient",
        "UIAutomationTypes",
    ];

    [Fact]
    public void Core_assembly_does_not_reference_desktop_ui_assemblies()
    {
        Assembly core = typeof(CoreInfo).Assembly;
        string[] referenced = core.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        string[] hits = referenced.Where(n => ForbiddenAssemblies.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
        Assert.True(hits.Length == 0, "Core が参照してはいけないアセンブリを参照している: " + string.Join(", ", hits));
    }

    [Fact]
    public void Core_csproj_has_no_windows_target_or_ui_framework_switches()
    {
        string path = Path.Combine(RepoPaths.Root, "src", "Ohanami.Core", "Ohanami.Core.csproj");
        var doc = XDocument.Load(path);
        var tfms = doc.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks").Select(e => e.Value.Trim()).ToList();
        Assert.NotEmpty(tfms);
        foreach (string tfm in tfms) Assert.DoesNotContain("-windows", tfm, StringComparison.OrdinalIgnoreCase);

        var switches = doc.Descendants().Where(e => e.Name.LocalName is "UseWPF" or "UseWindowsForms" or "UseWinUI" or "EnableWindowsTargeting").ToList();
        Assert.True(switches.Count == 0, "Core.csproj に UI 系のスイッチがある: " + string.Join(", ", switches.Select(e => e.Name.LocalName)));

        string[] forbiddenRefs = ForbiddenAssemblies.Concat(["Microsoft.WindowsDesktop.App"]).ToArray();
        var refs = doc.Descendants().Where(e => e.Name.LocalName is "Reference" or "FrameworkReference").Select(e => (string?)e.Attribute("Include") ?? "").ToList();
        foreach (string r in refs) Assert.DoesNotContain(r, forbiddenRefs);
    }
}
