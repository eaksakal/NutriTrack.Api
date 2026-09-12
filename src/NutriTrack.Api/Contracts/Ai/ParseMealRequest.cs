namespace NutriTrack.Api.Contracts.Ai;

public class ParseMealRequest
{
    public List<ChatMessage> Messages { get; set; } = [];
}

public class ChatMessage
{
    /// <summary>"user" oder "assistant". Der Verlauf ist die vollstaendige Wahrheit — der Server
    /// haelt keinen Gespraechszustand, damit es keine Sitzungen zum Aufraeumen gibt.</summary>
    public string Role { get; set; } = "user";

    public string Text { get; set; } = string.Empty;
}
