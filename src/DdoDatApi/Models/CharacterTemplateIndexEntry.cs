namespace DdoDatApi.Models;

/// <summary>
/// Compact structural character metadata used by the Dressing Room.  These are
/// candidates discovered from the game's own DbProperties records; Studio does
/// not hard-code a particular NPC as the canonical body for a race.
/// </summary>
public sealed class CharacterTemplateIndexEntry
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public uint RaceValue { get; set; }
    public string Race { get; set; } = "Unknown";
    public uint GenderValue { get; set; }
    public string Gender { get; set; } = "Unknown";
    public uint PhysObj { get; set; }
    public bool HasAppearanceClassList { get; set; }
    public int CandidateScore { get; set; }
}
