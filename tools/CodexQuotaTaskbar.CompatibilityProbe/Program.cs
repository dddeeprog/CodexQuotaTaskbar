using System.Text.Json;
using CodexQuotaTaskbar.CompatibilityProbe.Windows;
using CodexQuotaTaskbar.Core.Compatibility;

var identity = new WindowsIdentityReader().Read();
var evidence = new ModuleSignatureReader().Read();
var compatibility = CompatibilityPolicy.EvaluatePreflight(identity, evidence.Signature);

var report = new
{
    Windows = new
    {
        identity.MajorVersion,
        identity.MinorVersion,
        identity.BuildNumber,
        identity.UpdateBuildRevision,
        Architecture = identity.Architecture.ToString(),
        identity.IsComplete,
    },
    Explorer = evidence.Explorer,
    Modules = evidence.Modules,
    evidence.ModuleListKnown,
    evidence.XamlTypeParentSignature,
    evidence.MonitorCount,
    evidence.TaskbarHostCount,
    MonitorDpiValues = evidence.MonitorDpiValues,
    evidence.DisplayEvidenceKnown,
    CollectionStatus = evidence.CollectionStatus.ToString(),
    FailureReason = evidence.FailureReason.ToString(),
    Decision = compatibility.Decision.ToString(),
};

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;
