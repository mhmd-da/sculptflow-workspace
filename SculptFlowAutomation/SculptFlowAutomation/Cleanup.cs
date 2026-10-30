using SculptFlowAutomation.Core;
using SculptFlowAutomation.Http;

namespace SculptFlowAutomation;

public static class Cleanup
{
    /// <summary>Deletes every automation clinic still on the target (the main app only lists and deletes clinics
    /// named "[Automation] ..." whose users all have @sculptflow-automation.test emails).</summary>
    public static async Task<List<string>> SweepAsync(TargetEnvironment env)
    {
        var messages = new List<string>();
        using var admin = new SculptFlowClient(env.BaseUrl, _ => { }, new Dictionary<string, string>
        {
            ["X-Platform-Admin-Key"] = env.PlatformAdminKey!,
            ["X-Admin-Actor"] = "automation-tester",
        });

        var list = await admin.GetAsync("/api/platform-admin/automation/clinics");
        if (list.Status != 200)
        {
            messages.Add($"Couldn't list automation clinics (status {list.Status}). The target may not have the cleanup endpoint yet.");
            return messages;
        }

        var clinics = list.Array();
        if (clinics.Count == 0) messages.Add("No leftover test clinics.");
        foreach (var clinic in clinics)
        {
            var id = clinic?["id"]?.ToString();
            var name = clinic?["name"]?.ToString();
            var response = await admin.DeleteAsync($"/api/platform-admin/automation/clinics/{id}");
            messages.Add(response.Status == 204 ? $"Deleted \"{name}\"." : $"Couldn't delete \"{name}\" (status {response.Status}).{response.Snippet(200)}");
        }
        return messages;
    }
}
