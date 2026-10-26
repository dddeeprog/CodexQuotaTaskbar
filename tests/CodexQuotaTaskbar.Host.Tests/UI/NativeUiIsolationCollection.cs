namespace CodexQuotaTaskbar.Host.Tests.UI;

// WPF pack resources and native keyboard focus are process-wide. The new native
// UI fixtures must not race other STA fixtures that load the same XAML package.
[CollectionDefinition("Native UI isolation", DisableParallelization = true)]
public sealed class NativeUiIsolationCollection { }
