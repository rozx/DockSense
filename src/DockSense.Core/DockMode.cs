namespace DockSense.Core;

/// <summary>How the handheld is currently being used. Priority when several apply: Egpu &gt; Docked &gt; Handheld.</summary>
public enum DockMode
{
    /// <summary>No external display and no external GPU.</summary>
    Handheld,

    /// <summary>At least one external display is connected, but no external GPU.</summary>
    Docked,

    /// <summary>An external GPU (Thunderbolt / USB4 / OCuLink) is connected.</summary>
    Egpu,
}
