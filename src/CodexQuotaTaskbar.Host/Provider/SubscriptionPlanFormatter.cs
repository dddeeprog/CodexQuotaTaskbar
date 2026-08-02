namespace CodexQuotaTaskbar.Host.Provider;

internal static class SubscriptionPlanFormatter
{
    internal static string Format(string? rawPlan) => rawPlan?.Trim().ToLowerInvariant() switch
    {
        "free" => "Free",
        "go" => "Go",
        "plus" => "Plus",
        "pro" => "Pro",
        "prolite" => "Pro Lite",
        "team" => "Team",
        "self_serve_business_prolite" => "Self Serve Business ProLite",
        "self_serve_business_usage_based" => "Self Serve Business Usage Based",
        "business" => "Business",
        "ent26" => "Enterprise",
        "enterprise_cbp_automation" => "Enterprise (Automation)",
        "enterprise_cbp_usage_based" => "Enterprise CBP Usage Based",
        "enterprise" or "hc" => "Enterprise",
        "education" or "edu" => "Edu",
        null or "" => "未知",
        _ => "其他",
    };
}
