namespace VAppCore;

/// <summary>
/// A scope permissions are held in: its registered type (<c>"project"</c>) and its id, in the canonical form of the
/// scope's key type (a <see cref="Guid"/> as <c>"d"</c>) — or null for a singleton scope such as the whole instance.
/// </summary>
public readonly record struct VScope(string Type, string? Id)
{
    public override string ToString() => Id is null ? Type : $"{Type}:{Id}";
}
