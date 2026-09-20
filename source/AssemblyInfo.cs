using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("F1 ERS Manager")]
[assembly: AssemblyDescription("ERS controller for supported F1 Manager games")]
[assembly: AssemblyProduct("F1 ERS Manager")]
[assembly: AssemblyCompany("SkaffaWilly")]
[assembly: AssemblyVersion(AppVersion.Numeric)]
[assembly: AssemblyFileVersion(AppVersion.Numeric)]
[assembly: AssemblyInformationalVersion(AppVersion.Number)]
[assembly: AssemblyCopyright("Copyright (c) 2026 Willem Plenter (SkaffaWilly)")]
[assembly: ComVisible(false)]

internal static class AppVersion
{
    internal const string Number = "1.2";
    internal const string Numeric = "1.2.0.0";
    internal const string WindowTitle = "F1 ERS Manager " + Number;
}
