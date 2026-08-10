namespace Agent.Common.Data.Entities;

/// <summary>
/// A reviewer comment anchored to a line of a diff (mission W3, orca-adoption).
/// Collected in the Diff Review panel and shipped back to the agent as a
/// structured follow-up prompt; persisted so a review survives across sessions.
/// </summary>
public class DiffComment
{
    public int Id { get; set; }

    /// <summary>Groups comments belonging to one review session / diff snapshot.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Repo-relative file path the comment anchors to.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>1-based line number within the diff side.</summary>
    public int LineNumber { get; set; }

    /// <summary>Which side of the diff the line belongs to: "old" or "new".</summary>
    public string Side { get; set; } = "new";

    /// <summary>The reviewer's comment text.</summary>
    public string CommentText { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True once this comment has been shipped to the agent.</summary>
    public bool Shipped { get; set; }
}
