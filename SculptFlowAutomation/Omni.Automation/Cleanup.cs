using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation;

public static class Cleanup
{
    /// <summary>Deletes every automation client still on the target (the main app only lists and deletes clients named
    /// "[Automation] ..." whose users all have @omni-automation.test emails).</summary>
    public static async Task<List<string>> SweepAsync(TargetEnvironment env)
    {
        var messages = new List<string>();
        using var admin = new OmniClient(env.BaseUrl, _ => { }, new Dictionary<string, string>
        {
            ["X-Platform-Admin-Key"] = env.PlatformAdminKey!,
            ["X-Admin-Actor"] = "omni-automation",
        });

        var list = await admin.GetAsync("/api/platform-admin/automation/clients");
        if (list.Status != 200)
        {
            messages.Add($"Couldn't list automation clients (status {list.Status}). The target may not have the cleanup endpoint.");
            return messages;
        }

        var clients = list.Array();
        if (clients.Count == 0) messages.Add("No leftover test clients.");
        foreach (var client in clients)
        {
            var id = client?["id"]?.ToString();
            var name = client?["name"]?.ToString();
            var response = await admin.DeleteAsync($"/api/platform-admin/automation/clients/{id}");
            messages.Add(response.Status == 204 ? $"Deleted \"{name}\"." : $"Couldn't delete \"{name}\" (status {response.Status}).{response.Snippet(200)}");
        }
        return messages;
    }
}
