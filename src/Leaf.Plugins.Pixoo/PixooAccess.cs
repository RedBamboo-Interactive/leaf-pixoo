using System.Security.Claims;
using System.Text.Json;

namespace Leaf.Plugins.Pixoo;

/// <summary>Uses host-validated claims only; it never decodes bearer text.</summary>
public sealed record PixooCaller(string UserId, string? AgentId)
{
    public static PixooCaller From(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue("sub");
        if (principal.Identity?.IsAuthenticated != true || principal.Identity.AuthenticationType == "LocalDefault"
            || !Guid.TryParse(subject, out _)) throw new UnauthorizedAccessException("Explicit user authentication is required.");
        var tokenUse = principal.FindFirstValue("token_use");
        if (tokenUse is null) return new(subject!, null);
        if (tokenUse != "execution") throw new UnauthorizedAccessException("Execution identity is invalid.");
        try
        {
            using var document = JsonDocument.Parse(principal.FindFirstValue("execution_identity") ?? "null");
            var root = document.RootElement;
            var beneficiary = root.GetProperty("beneficiary");
            if (beneficiary.GetProperty("kind").GetString() != "user" || beneficiary.GetProperty("id").GetString() != subject)
                throw new UnauthorizedAccessException("Execution beneficiary mismatch.");
            var actor = root.GetProperty("actor");
            if (actor.GetProperty("kind").GetString() == "agent")
            {
                var id = actor.TryGetProperty("entityId", out var entity) ? entity.GetString() : actor.GetProperty("id").GetString();
                if (!Guid.TryParse(id, out _)) throw new UnauthorizedAccessException("Agent entity identity is required.");
                return new(subject!, id);
            }
            var app = root.GetProperty("app");
            if (actor.GetProperty("kind").GetString() != "app" || actor.GetProperty("id").GetString() != PixooPlugin.Id
                || app.GetProperty("id").GetString() != PixooPlugin.Id)
                throw new UnauthorizedAccessException("Pixoo app identity is required.");
            return new(subject!, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new UnauthorizedAccessException("Invalid execution identity."); }
    }
}
