namespace Content.Shared._NF.Access;

/// <summary>
///   When applied to an ID card console console, indicates that this console is shuttle-specific.
///   This means it may be used by captains to assign shuttle-specific roles to their crew people.
/// </summary>
[RegisterComponent]
public sealed partial class ShuttleIdCardConsoleComponent : Component
{
    /// <summary>
    ///   If true, this console can only be used to change the job state of the target ID and not the name/other fields.
    /// </summary>
    [DataField("jobChangeOnly")]
    public bool JobChangeOnly = true;

    /// <summary>
    ///   If true, this shuttle console adds a suffix to the job of the target ID, e.g. "Passenger" becomes "Passenger (PR-242)"
    /// </summary>
    [DataField("addShuttleSuffix")]
    public bool AddShuttleSuffix = true;

    /// <summary>
    ///   If true, requires the interacting player to use an ID with a shuttle deed bound to the current shuttle.
    /// </summary>
    [DataField("requiresCaptainAccess")]
    public bool RequiresCaptainAccess = true;
}
